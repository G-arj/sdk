// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Tools.Internal;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.IO;
using Microsoft.CodeAnalysis.Emit;
using System.Reflection.Metadata;
using System.Text.Json;
using Microsoft.Build.Locator;

namespace Microsoft.DotNet.Watcher.Tools
{
    public class CompilationHandler
    {
        private Task<(MSBuildWorkspace, Project)> _initializeTask;
        private EmitBaseline _emitBaseline;

        private readonly IReporter _reporter;
        private readonly CompilationChangeMaker _compilationChangeMaker;

        public CompilationHandler(IReporter reporter)
        {
            _reporter = reporter;
            _compilationChangeMaker = new CompilationChangeMaker(reporter);
        }

        public ValueTask InitializeAsync(DotNetWatchContext context)
        {
            if (context.FileSet.IsNetCoreApp31OrNewer) // needs to be net5.0
            {
                // Todo: figure this out for multi-project workspaces
                var project = context.FileSet.First(f => f.FilePath.EndsWith(".csproj", StringComparison.Ordinal));
                _initializeTask =  CreateMSBuildProject(project.FilePath, _reporter);

                context.ProcessSpec.EnvironmentVariables["COMPLUS_ForceEnc"] = "1";
                context.ProcessSpec.EnvironmentVariables["COMPlus_MD_DeltaCheck"] = "0";
            }

            return default;
        }

        public async ValueTask<bool> TryHandleFileChange(DotNetWatchContext context, FileItem file, CancellationToken cancellationToken)
        {
            if (!file.FilePath.EndsWith(".cs", StringComparison.Ordinal))
            {
                return false;
            }

            var (_workspace, _project) = await _initializeTask;

            var possiblyChangedDocuments = new List<(CodeAnalysis.Document oldDoc, CodeAnalysis.Document newDoc)>();

            // Read updated document
            if (!string.IsNullOrEmpty(file.FilePath))
            {
                var documentToUpdate = _project.Documents.FirstOrDefault(d => d.FilePath == file.FilePath);
                if (documentToUpdate != null)
                {
                    var text = await ReadFileTextWithRetry(file.FilePath);
                    var updatedDocument = documentToUpdate.WithText(SourceText.From(text, Encoding.UTF8));
                    var diff = await updatedDocument.GetTextChangesAsync(documentToUpdate);
                    _reporter.Verbose(string.Join(" ", diff.Select(d => d.NewText)));
                    possiblyChangedDocuments.Add((documentToUpdate, updatedDocument));
                    _project = updatedDocument.Project;
                }
            }

            if (_emitBaseline is null)
            {
                var initialPeStream = new MemoryStream();
                var initialPdbStream = new MemoryStream();
                var baselineCompilation = await _project.GetCompilationAsync(cancellationToken);
                var emitResult = baselineCompilation.Emit(initialPeStream, initialPdbStream);

                initialPeStream.Seek(0, SeekOrigin.Begin);
                var baselineMetadata = ModuleMetadata.CreateFromStream(initialPeStream);
                _emitBaseline = EmitBaseline.CreateInitialBaseline(baselineMetadata, handle => default);
            }

            var semanticEdits = new List<SemanticEdit>();
            foreach (var (oldDoc, newDoc) in possiblyChangedDocuments)
            {
                var changes = await _compilationChangeMaker.GetChanges(oldDoc, newDoc, cancellationToken);
                if (changes.IsDefault)
                {
                    _reporter.Verbose("Rude edit detected.");
                    return false;
                }
                else
                {
                    foreach (var change in changes)
                    {
                        semanticEdits.Add(change);
                    }
                }
            }

            if (!semanticEdits.Any())
            {
                _reporter.Verbose("No edits detected in the change");
                return false;
            }

            using var metaStream = new MemoryStream();
            using var ilStream = new MemoryStream();
            using var pdbStream = new MemoryStream();
            var updatedMethods = new List<MethodDefinitionHandle>();
            var updatedCompilation = await _project.GetCompilationAsync();
            var emitDifferenceResult = updatedCompilation.EmitDifference(_emitBaseline, semanticEdits, metaStream, ilStream, pdbStream, updatedMethods);
            if (emitDifferenceResult.Baseline != null)
            {
                _emitBaseline = emitDifferenceResult.Baseline;
            }

            var diagnostics = emitDifferenceResult.Diagnostics;
            if (diagnostics.Any())
            {
                foreach (var diagnostic in diagnostics)
                {
                    _reporter.Verbose(diagnostic.GetMessage());
                }

                return false;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(new UpdateDelta
            {
                ModulePath = _emitBaseline.OriginalMetadata.Name,
                MetaBytes = metaStream.ToArray(),
                IlBytes = ilStream.ToArray(),
                PdbBytes = pdbStream.ToArray(),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await context.BrowserRefreshServer.SendMessage(bytes);

            return true;
        }

        private static async Task<(MSBuildWorkspace, Project)> CreateMSBuildProject(string projectPath, IReporter reporter)
        {
            var instance = MSBuildLocator.QueryVisualStudioInstances().First();

            reporter.Verbose($"Using MSBuild at '{instance.MSBuildPath}' to load projects.");

            // NOTE: Be sure to register an instance with the MSBuildLocator
            //       before calling MSBuildWorkspace.Create()
            //       otherwise, MSBuildWorkspace won't MEF compose.
            MSBuildLocator.RegisterInstance(instance);

            Console.WriteLine($"Opening project at {projectPath}...");
            var msw = MSBuildWorkspace.Create();

            msw.WorkspaceFailed += (_sender, diag) =>
            {
                var warning = diag.Diagnostic.Kind == WorkspaceDiagnosticKind.Warning;
                if (!warning)
                {
                    Console.WriteLine($"msbuild failed opening project {projectPath}");
                }

                Console.WriteLine($"MSBuildWorkspace {diag.Diagnostic.Kind}: {diag.Diagnostic.Message}");

                if (!warning)
                {
                    throw new InvalidOperationException("failed workspace");
                }
            };

            var project = await msw.OpenProjectAsync(projectPath);
            return (msw, project);
        }

        private static async ValueTask<string> ReadFileTextWithRetry(string path)
        {
            for (var attemptIndex = 0; attemptIndex < 10; attemptIndex++)
            {
                try
                {
                    return File.ReadAllText(path);
                }
                // Presumably this is not the right way to handle this
                catch (IOException ex) when (ex.Message.Contains("it is being used by another process"))
                {
                    await Task.Delay(100);
                }
            }

            throw new InvalidOperationException($"Unabled to open {path} because it is in use");
        }

        private readonly struct UpdateDelta
        {
            public string Type => "UpdateCompilation";

            public string ModulePath { get; init;  }
            public byte[] MetaBytes { get; init; }
            public byte[] IlBytes { get; init; }

            public byte[] PdbBytes { get; init; }
        }
    }
}

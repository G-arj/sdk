// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

internal class StartupHook
{
    private static readonly ApplyUpdateDelegate? _applyHotReloadUpdate = GetApplyUpdate();

    public static void Initialize()
    {
        // This method exists to make startup hook load successfully. We do not need to do anything interesting here.

        _ = Task.Run(async () =>
        {
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_AUTO_RELOAD_WS_ENDPOINT")!;
            var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(env), default);
            var buffer = new byte[4 * 1024 * 1024];
            while (client.State == WebSocketState.Open)
            {
                var receive = await client.ReceiveAsync(buffer, default);
                if (receive.CloseStatus is not null)
                {
                    System.Console.WriteLine(receive.CloseStatus);
                    break;
                }

                UpdateDelta update;
                try
                {
                    update = JsonSerializer.Deserialize<UpdateDelta>(buffer.AsSpan(0, receive.Count), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                }
                catch 
                {
                    // Ignore these. It's probably a message for the browser.
                    continue;
                }

                if (update.Type != "UpdateCompilation")
                {
                    continue;
                }
                Console.WriteLine("Attempting to apply diff.");
                try
                {
                    ApplyChangesToAssembly(update.ModulePath, update.MetaBytes, update.IlBytes);
                    Console.WriteLine("Applied diff");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }

            Console.WriteLine("Exited update loop");
        });
    }

    private static void ApplyChangesToAssembly(string assemblyName, byte[] deltaMeta, byte[] deltaIl)
    {
        if (_applyHotReloadUpdate is null)
        {
            throw new NotSupportedException("ApplyUpdate is not supported.");
        }

        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((a) => !a.IsDynamic && Path.GetFileName(a.Location) == assemblyName);
        _applyHotReloadUpdate(assembly, new ReadOnlySpan<byte>(deltaMeta), new ReadOnlySpan<byte>(deltaIl), ReadOnlySpan<byte>.Empty);
    }

    private static ApplyUpdateDelegate? GetApplyUpdate()
    {
        var applyUpdateMethod = typeof(System.Reflection.Metadata.AssemblyExtensions).GetMethod("ApplyUpdate", BindingFlags.Public | BindingFlags.Static);
        if (applyUpdateMethod is null)
        {
            return null;
        }

        var applyHotReloadUpdate = (ApplyUpdateDelegate)applyUpdateMethod.CreateDelegate(typeof(ApplyUpdateDelegate))!;
        return applyHotReloadUpdate;
    }

    private delegate void ApplyUpdateDelegate(Assembly assembly, ReadOnlySpan<byte> metadataDelta, ReadOnlySpan<byte> ilDelta, ReadOnlySpan<byte> pdbDelta);

    private struct UpdateDelta
    {
        public string Type { get; set; }

        public string ModulePath { get; set; }
        public byte[] MetaBytes { get; set; }
        public byte[] IlBytes { get; set; }
    }
}

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using FocoTotal.Core;

namespace FocoTotal.Service;

/// <summary>Pipe nomeado local para o app de configuração. Acesso pela rede é negado.</summary>
public sealed class PipeServer : BackgroundService
{
    private readonly ILogger<PipeServer> _log;
    private readonly Coordinator _coordinator;

    public PipeServer(ILogger<PipeServer> log, Coordinator coordinator)
    {
        _log = log; _coordinator = coordinator;
    }

    private static PipeSecurity CreateSecurity()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var security = CreateSecurity();
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(IpcClient.PipeName, PipeDirection.InOut, 16,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(stoppingToken);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => HandleClientAsync(connected, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Erro no pipe de controle");
                await Task.Delay(1000, stoppingToken);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                var request = await IpcClient.ReadMessageAsync<IpcRequest>(pipe, cts.Token);
                if (request is null) return;

                var isAdmin = false;
                pipe.RunAsClient(() =>
                {
                    using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                    isAdmin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                });

                var response = _coordinator.Handle(request, isAdmin);
                await IpcClient.WriteMessageAsync(pipe, response, cts.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
            {
                _log.LogDebug(ex, "Cliente do pipe desconectado");
            }
        }
    }
}

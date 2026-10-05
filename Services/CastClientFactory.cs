using System.Reflection;
using System.Timers;
using Sharpcaster;
using Sharpcaster.Channels;

namespace BabyMonitarr.Backend.Services;

/// <summary>
/// Creates Sharpcaster clients whose heartbeat cannot take the process down.
/// <para>
/// Sharpcaster 3.0.0 pings the device from an <c>async void</c> timer handler. When the socket
/// breaks under it (the device closed the connection after its receive loop died on a message it
/// could not parse), the write throws on the thread pool and .NET terminates the whole backend.
/// This swaps that handler for one that does the same ping and timeout, but turns any failure
/// into a disconnect, which raises <see cref="ChromecastClient.Disconnected"/> as normal.
/// </para>
/// Ceiling: reaches into three private members of HeartbeatChannel, verified against 3.0.0.
/// Creating a client throws if an upgrade renames them; drop this once upstream catches its
/// timer exceptions.
/// </summary>
public static class CastClientFactory
{
    private const string PingPayload = "{\"type\":\"PING\"}";

    private static readonly FieldInfo TimerField = PrivateField("_timer");
    private static readonly FieldInfo TriedToPingField = PrivateField("_triedToPing");
    private static readonly MethodInfo LibraryTimerElapsed =
        typeof(HeartbeatChannel).GetMethod("TimerElapsed", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw MissingMember("TimerElapsed");

    public static ChromecastClient Create(ILoggerFactory loggerFactory)
    {
        var client = new ChromecastClient(loggerFactory.CreateLogger<ChromecastClient>());
        GuardHeartbeat(client, loggerFactory.CreateLogger(typeof(CastClientFactory)));
        return client;
    }

    private static void GuardHeartbeat(ChromecastClient client, ILogger logger)
    {
        var heartbeat = client.HeartbeatChannel;
        var timer = (System.Timers.Timer)TimerField.GetValue(heartbeat)!;
        timer.Elapsed -= (ElapsedEventHandler)Delegate.CreateDelegate(
            typeof(ElapsedEventHandler), heartbeat, LibraryTimerElapsed);
        timer.Elapsed += async (_, _) =>
        {
            try
            {
                // The library resets this flag itself whenever the device answers.
                if ((bool)TriedToPingField.GetValue(heartbeat)!)
                {
                    logger.LogInformation("Cast device {Name} stopped answering heartbeats", client.FriendlyName);
                    await DisconnectQuietlyAsync(client, logger);
                    return;
                }

                await client.SendAsync(null, heartbeat.Namespace, PingPayload, "receiver-0");
                TriedToPingField.SetValue(heartbeat, true);
            }
            catch (Exception ex)
            {
                logger.LogInformation("Cast heartbeat to {Name} failed: {Message}", client.FriendlyName, ex.Message);
                await DisconnectQuietlyAsync(client, logger);
            }
        };
    }

    private static async Task DisconnectQuietlyAsync(ChromecastClient client, ILogger logger)
    {
        try
        {
            await client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error disconnecting cast client {Name} after a heartbeat failure", client.FriendlyName);
        }
    }

    private static FieldInfo PrivateField(string name) =>
        typeof(HeartbeatChannel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw MissingMember(name);

    private static InvalidOperationException MissingMember(string name) =>
        new($"Sharpcaster's HeartbeatChannel no longer has '{name}'; revisit {nameof(CastClientFactory)}.");
}

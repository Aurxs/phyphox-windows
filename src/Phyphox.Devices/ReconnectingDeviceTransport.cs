using System.Runtime.CompilerServices;
namespace Phyphox.Devices;

public sealed record ReconnectPolicy(int Attempts=3,int InitialDelayMilliseconds=500,int MaximumDelayMilliseconds=5000);
/// <summary>Opt-in bounded reconnect. Recreates the connection/profile only; never retries writes or replays control commands.</summary>
public sealed class ReconnectingDeviceTransport(IDeviceTransport inner,ReconnectPolicy? policy=null) : IDeviceTransport
{
    private readonly ReconnectPolicy policy=Validate(policy??new());
    private DeviceDescriptor? device;
    private DeviceProfile? profile;
    private CancellationTokenSource lifetime=new();
    public event Action<int,Exception>? Reconnecting;
    public event Action? Reconnected;
    public TransportKind Kind=>inner.Kind;
    public bool IsAvailable=>inner.IsAvailable;
    public DeviceStatus Status=>inner.Status;
    private static ReconnectPolicy Validate(ReconnectPolicy p)=>p.Attempts is >=0 and <=20 && p.InitialDelayMilliseconds>0 && p.MaximumDelayMilliseconds>=p.InitialDelayMilliseconds?p:throw new ArgumentException("Invalid reconnect policy.");
    public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken=default)=>inner.ScanAsync(cancellationToken);
    public async Task ConnectAsync(DeviceDescriptor device,DeviceProfile profile,CancellationToken cancellationToken=default) {
        await inner.ConnectAsync(device,profile,cancellationToken);this.device=device;this.profile=profile;
        lifetime.Dispose();lifetime=new();
    }
    public async IAsyncEnumerable<DeviceFrame> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken=default) {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
        while(true) {
            Exception? failure=null;
            await using(var reader=inner.ReadAsync(linked.Token).GetAsyncEnumerator(linked.Token)) {
                while(true) {
                    bool next;
                    try {next=await reader.MoveNextAsync();}
                    catch(IOException ex){failure=ex;break;}
                    catch(System.ComponentModel.Win32Exception ex){failure=ex;break;}
                    if(!next)break;
                    yield return reader.Current;
                }
            }
            if(failure==null)yield break;
            bool restored=false;
            for(int attempt=1;attempt<=policy.Attempts;attempt++) {
                linked.Token.ThrowIfCancellationRequested();Reconnecting?.Invoke(attempt,failure);
                await inner.DisconnectAsync(linked.Token);
                int delay=(int)Math.Min(policy.MaximumDelayMilliseconds,policy.InitialDelayMilliseconds*Math.Pow(2,attempt-1));
                await Task.Delay(delay,linked.Token);
                try {
                    await inner.ConnectAsync(device??throw new InvalidOperationException("No previous device."),profile??throw new InvalidOperationException("No previous profile."),linked.Token);
                    restored=true;Reconnected?.Invoke();break;
                }catch(IOException ex){failure=ex;}catch(System.ComponentModel.Win32Exception ex){failure=ex;}
            }
            if(!restored)throw new IOException("Device reconnect budget exhausted; measurement gap must be recorded.",failure);
        }
    }
    public Task WriteAsync(ReadOnlyMemory<byte> data,CancellationToken cancellationToken=default)=>inner.WriteAsync(data,cancellationToken);
    public async Task DisconnectAsync(CancellationToken cancellationToken=default) {await lifetime.CancelAsync();await inner.DisconnectAsync(cancellationToken);}
    public async ValueTask DisposeAsync(){await DisconnectAsync();lifetime.Dispose();await inner.DisposeAsync();}
}

using System.Text.Json;
using Phyphox.Server;
static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
static void Check(bool value,string message){if(!value)throw new Exception(message);}
static async Task Reject(Func<Task> action){try{await action();}catch(InvalidOperationException){return;}catch(IOException){return;}throw new Exception("Unauthorized lease operation accepted");}
var clock=new TestClock();bool enabled=false;int stops=0;
using var lease=new PhoneBridgeLease(()=>enabled,()=>{stops++;return Task.CompletedTask;},clock);
await Reject(async()=>await lease.Claim());
enabled=true;
var owner=Json(await lease.Claim()).GetProperty("leaseId").GetString()!;
await Reject(async()=>await lease.Claim());
await Reject(()=>{lease.IssueSignalTicket("wrong");return Task.CompletedTask;});
string Ticket()=>Json(lease.IssueSignalTicket(owner)).GetProperty("token").GetString()!;
var ticket=Ticket();
await Reject(()=>{lease.ConsumeSignalTicket("wrong");return Task.CompletedTask;});
var authority=lease.ConsumeSignalTicket(ticket);Check(authority.LeaseId==owner,"ticket belongs to lease");
await Reject(()=>{lease.ConsumeSignalTicket(ticket);return Task.CompletedTask;});
ticket=Ticket();
for(int i=0;i<3;i++){clock.Advance(4);lease.Heartbeat(owner);}
await Reject(()=>{lease.ConsumeSignalTicket(ticket);return Task.CompletedTask;});
ticket=Ticket();await lease.Release(owner);Check(authority.Cancellation.IsCancellationRequested,"release terminates authorized sockets");
await Reject(()=>{lease.ConsumeSignalTicket(ticket);return Task.CompletedTask;});
await Reject(()=>lease.RunOwned(owner,()=>throw new Exception("stale mutation ran"),CancellationToken.None));
owner=Json(await lease.Claim()).GetProperty("leaseId").GetString()!;
clock.Advance(6);await lease.ExpireAsync();Check(stops==2,"release and timeout stop exactly once");
await Reject(()=>{lease.Heartbeat(owner);return Task.CompletedTask;});
owner=Json(await lease.Claim()).GetProperty("leaseId").GetString()!;
ticket=Ticket();await lease.ChangeGateway(()=>{enabled=false;return Task.FromResult<object>(new{enabled});},CancellationToken.None);
await Reject(()=>{lease.ConsumeSignalTicket(ticket);return Task.CompletedTask;});
await Reject(async()=>await lease.Claim());
Check(stops==3,"disable revokes capture");
Console.WriteLine("PASS: local bridge disabled/duplicate claim, ticket unauthorized/reuse/expiry, owner release/timeout, stale mutation and gateway disable.");
sealed class TestClock:TimeProvider
{
    DateTimeOffset now=DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow()=>now;
    public override long GetTimestamp()=>now.UtcTicks;
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public void Advance(int seconds)=>now=now.AddSeconds(seconds);
}

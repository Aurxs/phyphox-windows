namespace Phyphox.Devices;

public static class BleExperimentDownloader
{
    public static readonly Guid Service=new("cddf0001-30f7-4671-8b43-5e40ba53514a");
    public static readonly Guid Experiment=new("cddf0002-30f7-4671-8b43-5e40ba53514a");
    public static readonly Guid Control=new("cddf0003-30f7-4671-8b43-5e40ba53514a");
    /// <summary>Uses a dedicated transport. Returns CRC-verified raw XML/ZIP bytes; caller must still validate/import the file.</summary>
    public static async Task<byte[]> DownloadAsync(IBleDeviceTransport transport,DeviceDescriptor device,IProgress<(int Received,int? Expected)>? progress=null,CancellationToken cancellationToken=default) {
        bool control=false,subscribed=false;
        try {
            await transport.ConnectAsync(device,new DeviceProfile("official-phyphox-transfer",TransportKind.Ble,Ble:new BleSettings(Service,Experiment,Subscribe:false)),cancellationToken);
            var characteristics=await transport.GetCharacteristicsAsync(cancellationToken);
            var data=characteristics.FirstOrDefault(x=>x.Uuid==Experiment)??throw new IOException("phyphox experiment characteristic absent.");
            control=characteristics.Any(x=>x.Uuid==Control);
            subscribed=data.Notify;
            if(subscribed)await transport.SubscribeAsync(Experiment,false,cancellationToken);
            if(control)await transport.WriteCharacteristicAsync(Control,new byte[]{1},false,cancellationToken);
            var transfer=new BleExperimentTransfer();
            using var receiveTimeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using var notifications=transport.ReadValuesAsync(receiveTimeout.Token).GetAsyncEnumerator(receiveTimeout.Token);
            while(!transfer.Complete) {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(10));
                DeviceFrame frame;
                if(subscribed) {
                    receiveTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    if(!await notifications.MoveNextAsync())throw new EndOfStreamException("BLE transfer ended before announced length.");
                    receiveTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    if(notifications.Current.Characteristic!=Experiment)continue;
                    frame=notifications.Current.Frame;
                } else frame=await transport.ReadCharacteristicAsync(Experiment,timeout.Token);
                transfer.Receive(frame.Data);progress?.Report((transfer.Received,transfer.Expected));
            }
            return transfer.GetVerifiedPayload();
        } finally {
            if(subscribed) {try {await transport.UnsubscribeAsync(Experiment,CancellationToken.None);}catch(Exception){}}
            if(control) {try {await transport.WriteCharacteristicAsync(Control,new byte[]{0},false,CancellationToken.None);}catch(Exception){}}
            await transport.DisconnectAsync(CancellationToken.None);
        }
    }
}

/* Bounded main-thread delivery; microphone samples are never silently skipped during a run. */
class PhonePCMProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.epoch = 0;
    this.active = false;
    this.pending = false;
    this.pendingFrames = 0;
    this.offset = 0;
    this.buffer = new Float32Array(Math.round(sampleRate * 0.02));
    this.port.onmessage = ({data}) => {
      if (data.type === 'run') {
        this.epoch = data.epoch;
        this.active = data.active;
        this.pending = false;
        this.pendingFrames = 0;
        this.offset = 0;
      } else if (data.type === 'ack' && data.epoch === this.epoch) {
        this.pending = false;
        this.pendingFrames = 0;
      }
    };
  }
  process(inputs) {
    const channels = inputs[0];
    if (!this.active || !channels?.length || !channels[0]?.length) return true;
    // A pending delivery already occupies the one-block queue. The next completed
    // block is an explicit overrun rather than an unbounded MessagePort backlog.
    for (let i = 0; i < channels[0].length; i++) {
      let value = 0;
      for (const channel of channels) value += channel[i] || 0;
      this.buffer[this.offset++] = value / channels.length;
      if (this.offset === this.buffer.length) {
        if (this.pending) {
          this.active = false;
          this.port.postMessage({type:'overrun',epoch:this.epoch});
          return true;
        }
        const samples = this.buffer;
        this.buffer = new Float32Array(samples.length);
        this.offset = 0;
        this.pending = true;
        this.port.postMessage({type:'pcm',epoch:this.epoch,sampleRate,samples},[samples.buffer]);
      }
    }
    return true;
  }
}
registerProcessor('phyphox-phone-pcm', PhonePCMProcessor);

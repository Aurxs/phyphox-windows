// Same-origin AudioWorklet: no blob script or external dependency.

class PhyphoxPcmProcessor extends AudioWorkletProcessor {
 constructor() { super(); this.frames=[]; this.count=0; this.target=Math.max(128, Math.round(sampleRate / 10)); this.pending=false; this.port.onmessage=()=>{this.pending=false;}; }
 process(inputs) {
  const channels=inputs[0];
  if (!channels || !channels.length || !channels[0].length) return true;
  const mono=new Float32Array(channels[0].length);
  for (let c=0;c<channels.length;c++) for(let i=0;i<mono.length;i++) mono[i]+=channels[c][i]/channels.length;
  this.frames.push(mono); this.count+=mono.length;
  if(this.count>=this.target) {
   if(this.pending){this.port.postMessage({overrun:true});return false;}
   this.pending=true;
   const samples=new Float32Array(this.count); let offset=0;
   for(const frame of this.frames){samples.set(frame,offset);offset+=frame.length;}
   this.port.postMessage({samples,sampleRate},[samples.buffer]); this.frames=[]; this.count=0;
  }
  return true;
 }
}
registerProcessor('phyphox-real-pcm',PhyphoxPcmProcessor);

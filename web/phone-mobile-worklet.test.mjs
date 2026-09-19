import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';
const source = readFileSync(new URL('./public/phone-pcm-worklet.js',import.meta.url),'utf8');
function create() {
  let Processor;
  class AudioWorkletProcessor {port = {onmessage:null,messages:[],postMessage(value) {this.messages.push(value);}};}
  vm.runInNewContext(source,{AudioWorkletProcessor,sampleRate:1000,Float32Array,registerProcessor(_name,processor) {Processor = processor;}});
  const processor = new Processor();
  const control = data => processor.port.onmessage({data});
  const block = (value = 0.5, length = 20) => processor.process([[new Float32Array(length).fill(value)]]);
  return {processor,control,block,messages:processor.port.messages};
}
test('phone PCM captures real mono batches only during an enabled epoch',() => {
  const {processor,control,block,messages} = create();
  block(); assert.equal(messages.length,0);
  control({type:'run',epoch:1,active:true});
  processor.process([[new Float32Array(20).fill(1),new Float32Array(20).fill(-0.5)]]);
  assert.equal(messages.length,1); assert.equal(messages[0].epoch,1); assert.equal(messages[0].sampleRate,1000);
  assert.equal(messages[0].samples.length,20); assert.equal(messages[0].samples[0],0.25);
  control({type:'ack',epoch:1}); block(); assert.equal(messages.length,2);
  control({type:'run',epoch:2,active:false}); block(); assert.equal(messages.length,2);
});
test('phone PCM reports delivery overrun instead of silently losing audio',() => {
  const {control,block,messages} = create();
  control({type:'run',epoch:1,active:true}); block(); block();
  assert.equal(messages.length,2); assert.equal(messages[1].type,'overrun');
  block(); assert.equal(messages.length,2);
});
test('phone PCM rejects stale epoch acknowledgements and discards old partial samples',() => {
  const {control,block,messages} = create();
  control({type:'run',epoch:1,active:true}); block(1,10);
  control({type:'run',epoch:2,active:true}); block(0.3,20);
  assert.equal(messages.length,1); assert.equal(messages[0].epoch,2);
  assert.ok(messages[0].samples.every(value => Math.abs(value - 0.3) < 1e-6));
  control({type:'ack',epoch:1}); block(); assert.equal(messages[1].type,'overrun');
});

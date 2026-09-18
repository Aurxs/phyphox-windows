import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFileSync} from 'node:fs';
const browserPcmProcessor=readFileSync(new URL('./public/browser-pcm-worklet.js',import.meta.url),'utf8');
function processor(){let Processor;const packets=[];const context={sampleRate:2560,Float32Array,AudioWorkletProcessor:class{port={postMessage:m=>packets.push(m)}},registerProcessor:(_name,p)=>Processor=p};vm.runInNewContext(browserPcmProcessor,context);return {node:new Processor(),packets};}
test('browser PCM worklet emits no packet for absent input and batches real mono input',()=>{const {node,packets}=processor();node.process([]);node.process([[]]);assert.equal(packets.length,0);const frame=new Float32Array(128).fill(.25);node.process([[frame]]);assert.equal(packets.length,0);node.process([[frame]]);assert.equal(packets.length,1);assert.equal(packets[0].sampleRate,2560);assert.equal(packets[0].samples.length,256);assert.ok(packets[0].samples.every(v=>v===.25));});
test('browser PCM worklet averages actual stereo channels without amplification',()=>{const {node,packets}=processor();const left=new Float32Array(128).fill(.75),right=new Float32Array(128).fill(-.25);node.process([[left,right]]);node.process([[left,right]]);assert.ok(packets[0].samples.every(v=>v===.25));});

test('worklet stops on unacknowledged input backlog instead of sending a discontinuous timeline',()=>{const {node,packets}=processor();const frame=new Float32Array(128).fill(.5);node.process([[frame]]);node.process([[frame]]);node.process([[frame]]);assert.equal(node.process([[frame]]),false);assert.equal(packets.length,2);assert.equal(packets[1].overrun,true);});

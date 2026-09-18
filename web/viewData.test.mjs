import test from 'node:test';
import assert from 'node:assert/strict';
import {mappedText} from './src/viewData.ts';
test('official audio amplitude calibration: zero matches exact inclusive range',()=>{
 const maps=[{attributes:{min:'0',max:'0'},text:'NOT calibrated'},{attributes:{},text:'calibrated'}];
 assert.equal(mappedText(0,maps),'NOT calibrated');
 assert.equal(mappedText(1,maps),'calibrated');
 assert.equal(mappedText(NaN,maps),undefined);
});

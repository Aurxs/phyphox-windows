import test from 'node:test';
import assert from 'node:assert/strict';
import {pairGraphInputs} from './src/graphData.ts';
const p=(buffer,axis)=>({buffer,attributes:{axis}});
test('official graph-input-orders: y-before-x pairs by order',()=>{
 assert.deepEqual(pairGraphInputs([p('a','y'),p('t','x'),p('b','y'),p('u','x')]).map(v=>[v.x?.buffer,v.y.buffer]),[['t','a'],['u','b']]);
});
test('official graph-input-orders: shared and absent x',()=>{
 assert.deepEqual(pairGraphInputs([p('t','x'),p('a','y'),p('b','y')]).map(v=>v.x?.buffer),['t','t']);
 assert.equal(pairGraphInputs([p('a','y')])[0].x,undefined);
});
import {barBounds,paletteColor} from './src/graphData.ts';
test('official bar edges and width fraction',()=>{assert.deepEqual(barBounds(2,6,.5),[3,5]);assert.deepEqual(barBounds(2,6),[2,6]);});
test('map RGB palette interpolates stops independently of spatial cell mode',()=>{assert.deepEqual(paletteColor(['000000','ffffff'],.5),[128,128,128]);assert.deepEqual(paletteColor(['black','orange','white'],.5),[255,126,34]);});

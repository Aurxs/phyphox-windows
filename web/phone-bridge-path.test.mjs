import test from 'node:test';
import assert from 'node:assert/strict';
import {hasDirectSelectedPath} from './src/phone/bridgeProtocol.ts';
function path(local,remote){return new Map([
 ['transport',{type:'transport',selectedCandidatePairId:'chosen'}],
 ['chosen',{id:'chosen',type:'candidate-pair',state:'succeeded',nominated:true,localCandidateId:'a',remoteCandidateId:'b'}],
 ['a',{candidateType:local}],['b',{candidateType:remote}]
]);}
test('real Chrome host-only negotiation can select a peer-reflexive direct endpoint',()=>{
 assert.equal(hasDirectSelectedPath(path('prflx','host')),true);
 assert.equal(hasDirectSelectedPath(path('host','host')),true);
});
test('a selected relay is rejected even if an old nominated host pair exists',()=>{
 const stats=path('host','relay');
 stats.set('old',{id:'old',type:'candidate-pair',state:'succeeded',nominated:true,localCandidateId:'a',remoteCandidateId:'a'});
 assert.equal(hasDirectSelectedPath(stats),false);
 assert.equal(hasDirectSelectedPath(path('srflx','host')),false);
 assert.equal(hasDirectSelectedPath(new Map()),false);
});

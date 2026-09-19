// SPDX-License-Identifier: GPL-3.0-only
export type MotionKind='accelerometer'|'gyroscope'|'linear_acceleration';
export type PhoneKind=MotionKind|'audio'|'camera';
export const motionKinds:MotionKind[]=['accelerometer','gyroscope','linear_acceleration'];
export type PhoneCapabilities={motion?:Partial<Record<MotionKind,boolean>>;audio?:{sampleRate:number};camera?:{width:number;height:number}};
export function capabilities(value:unknown):PhoneCapabilities {
 const v=value as PhoneCapabilities|null;
 if(!v||typeof v!=='object')throw new Error('手机能力消息无效。');
 const result:PhoneCapabilities={motion:{}};
 for(const k of motionKinds)result.motion![k]=v.motion?.[k]===true;
 if(v.audio){if(!Number.isInteger(v.audio.sampleRate)||v.audio.sampleRate<8000||v.audio.sampleRate>192000)throw new Error('手机音频采样率无效。');result.audio={sampleRate:v.audio.sampleRate};}
 if(v.camera){const {width,height}=v.camera;if(!Number.isInteger(width)||!Number.isInteger(height)||width<1||height<1||width>1920||height>1080)throw new Error('手机图像尺寸无效。');result.camera={width,height};}
 return result;
}
export function isHostCandidate(candidate:string):boolean {
 return candidate===''||/\btyp host(?:\s|$)/.test(candidate);
}
export function checkDescription(value:RTCSessionDescriptionInit,expected:'offer'|'answer') {
 if(value?.type!==expected||typeof value.sdp!=='string'||value.sdp.length>60000)throw new Error('配对连接描述无效。');
 for(const line of value.sdp.split(/\r?\n/))if(line.startsWith('a=candidate:')&&!isHostCandidate(line.slice(2)))throw new Error('仅允许设备直接连接。');
}
export function hasCapability(c:PhoneCapabilities,kind:string):boolean {
 return kind==='audio'?!!c.audio:kind==='camera'?!!c.camera:motionKinds.includes(kind as MotionKind)&&c.motion?.[kind as MotionKind]===true;
}
export function hasDirectSelectedPath(stats:Pick<RTCStatsReport,'forEach'|'get'>):boolean {
 const selected=new Set<string>();
 stats.forEach(s=>{if(s.type==='transport'&&s.selectedCandidatePairId)selected.add(s.selectedCandidatePairId);});
 if(!selected.size)stats.forEach(s=>{if(s.type==='candidate-pair'&&s.state==='succeeded'&&s.nominated)selected.add(s.id);});
 // ICE can discover a peer-reflexive endpoint even when both sides only
 // advertise host candidates. It remains a direct path, not a TURN relay.
 const direct=(kind:unknown)=>kind==='host'||kind==='prflx';
 return selected.size>0&&[...selected].every(id=>{
  const pair=stats.get(id);
  return pair?.state==='succeeded'&&direct(stats.get(pair.localCandidateId)?.candidateType)&&direct(stats.get(pair.remoteCandidateId)?.candidateType);
 });
}

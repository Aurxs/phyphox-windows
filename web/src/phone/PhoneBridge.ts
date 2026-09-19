// SPDX-License-Identifier: GPL-3.0-only
import {request,type Snapshot} from '../api';
import {capabilities,checkDescription,hasCapability,hasDirectSelectedPath,isHostCandidate,type PhoneCapabilities,type PhoneKind} from './bridgeProtocol';

export type PhoneSnapshot=Snapshot & {phoneRunId?:string;phoneMotion?:{captureId:string;inputIndex:number;kind:string;ready:boolean}[];browserMedia?:{captureId:string;inputIndex:number;kind:string;source?:string}[]};
export type BridgeView={status:string;error:string;link:string;expiresAt:string;name:string;pendingName:string;connected:boolean;connecting:boolean;capabilities:PhoneCapabilities;bound?:{kind:PhoneKind;inputIndex:number;captureId:string};accepted:number;rtt?:number};
export const initialBridgeView=():BridgeView=>({status:'尚未连接手机',error:'',link:'',expiresAt:'',name:'',pendingName:'',connected:false,connecting:false,capabilities:{},accepted:0});

export class PhoneBridge {
 view=initialBridgeView();
 private socket?:WebSocket;
 private pc?:RTCPeerConnection;
 private channel?:RTCDataChannel;
 private lease?:string;
 private timer?:ReturnType<typeof setInterval>;
 private deadline=0;
 private lastPong=0;
 private candidates:RTCIceCandidateInit[]=[];
 private signalQueue=Promise.resolve();
 private snapshot:PhoneSnapshot|null=null;
 private runKey='';
 private generation=0;
 private accepting=false;
 private bindingBusy=false;
 private connectBusy=false;
 private inflight=false;
 private heartbeatBusy=false;
 constructor(private changed:(view:BridgeView)=>void,private onSession:(snapshot:Snapshot)=>void){}
 private publish(values:Partial<BridgeView>={}){this.view={...this.view,...values};this.changed(this.view);}
 private async local<T>(path:string,body?:unknown):Promise<T>{return request<T>(path,body,{'X-Phyphox-Phone-Lease':this.lease??''});}
 private signal(data:unknown){if(this.socket?.readyState===WebSocket.OPEN)this.socket.send(JSON.stringify({type:'signal',data}));}
 private send(data:unknown){if(this.channel?.readyState==='open')this.channel.send(JSON.stringify(data));}
 setSnapshot(snapshot:PhoneSnapshot|null){
  this.snapshot=snapshot;
  const bound=this.view.bound;
  if(bound&&snapshot){
   const alive=[...(snapshot.phoneMotion??[]),...(snapshot.browserMedia??[])].some(c=>c.captureId===bound.captureId);
   if(!alive){this.publish({bound:undefined});this.send({type:'release'});}
  }
  this.syncRun();
 }
 private syncRun(){
  const runId=this.snapshot?.phoneRunId??null,active=!!this.view.bound&&!!runId,measuring=this.snapshot?.status==='running';
  const key=JSON.stringify([runId,active,measuring]);
  if(this.view.connected&&key!==this.runKey){this.runKey=key;this.inflight=false;this.send({type:'run',runId,active,measuring});}
 }
 async connect(origin:string){
  if(this.connectBusy)return;
  this.connectBusy=true;
  try {
  await this.disconnect();
  const generation=++this.generation;
  this.publish({...initialBridgeView(),connecting:true,status:'正在建立配对…'});
  try{
   const claim=await request<{leaseId:string}>('phone/claim',{});
   if(generation!==this.generation){await request('phone/release',{leaseId:claim.leaseId});return;}
   this.lease=claim.leaseId;
   const ticket=await request<{token:string}>('phone/signal-ticket',{leaseId:claim.leaseId});
   if(generation!==this.generation)return;
   const url=new URL('/phone-signal',window.location.origin);url.protocol=url.protocol==='https:'?'wss:':'ws:';
   const socket=this.socket=new WebSocket(url);this.deadline=Date.now()+15000;
   socket.onopen=()=>{if(generation===this.generation){socket.send(JSON.stringify({type:'authenticate',token:ticket.token}));socket.send(JSON.stringify({type:'create',version:1}));}};
   socket.onmessage=event=>{
    this.signalQueue=this.signalQueue.then(async()=>{
     if(generation!==this.generation)return;
     if(typeof event.data!=='string'||event.data.length>65536)throw new Error('配对消息过大。');
     const message=JSON.parse(event.data);
     if(message.type==='created'){
      if(typeof message.roomId!=='string'||typeof message.joinToken!=='string'||!Number.isFinite(Date.parse(message.expiresAt)))throw new Error('配对服务响应无效。');
      const link=new URL('/phone.html',origin);link.hash=new URLSearchParams({room:message.roomId,token:message.joinToken}).toString();
      this.deadline=Date.parse(message.expiresAt);
      this.publish({link:link.href,expiresAt:message.expiresAt,status:'请用手机系统相机扫码'});
     }else if(message.type==='join-request'){
      this.deadline=Date.now()+60000;
      this.publish({pendingName:String(message.name||'手机').slice(0,80),link:'',status:'请确认接入手机'});
     }else if(message.type==='paired'){
      this.publish({name:this.view.pendingName,pendingName:'',status:'正在建立设备直连…'});this.deadline=Date.now()+20000;await this.offer(generation);
     }else if(message.type==='signal')await this.remoteSignal(message.data);
     else if(message.type==='error')throw new Error(String(message.message||'配对失败'));
     else if(message.type==='peer-left'&&!this.view.connected)throw new Error('配对已结束，请刷新二维码后重试。');
    }).catch(error=>{if(generation===this.generation&&!this.view.connected)void this.fail(error);});
   };
   socket.onerror=()=>{if(generation===this.generation&&!this.view.connected)void this.fail(new Error('无法连接本机配对服务，请确认局域网入口已启用。'));};
   socket.onclose=()=>{if(generation!==this.generation)return;if(this.view.connected)this.publish({status:'设备直连中 · 配对服务已离线'});else void this.fail(new Error('配对连接关闭，请重新连接。'));};
   this.timer=setInterval(()=>void this.tick(generation),1000);
  }catch(error){if(generation===this.generation)await this.fail(error);}
  } finally {this.connectBusy=false;}
 }
 confirm(accept:boolean){
  if(!this.view.pendingName||this.accepting)return;
  this.accepting=true;this.socket?.send(JSON.stringify({type:accept?'accept':'reject'}));
  if(!accept)void this.disconnect();
 }
 private async offer(generation:number){
  if(this.pc)throw new Error('重复连接协商。');
  const pc=this.pc=new RTCPeerConnection({iceServers:[]});
  pc.onicecandidate=e=>{if(generation===this.generation&&e.candidate&&isHostCandidate(e.candidate.candidate))this.signal({candidate:e.candidate.toJSON()});};
  pc.onconnectionstatechange=()=>{if(generation===this.generation&&['failed','closed','disconnected'].includes(pc.connectionState))void this.fail(new Error('手机直连已中断；请检查页面、Wi-Fi 隔离和防火墙策略。'));};
  const channel=this.channel=pc.createDataChannel('phone-v1',{ordered:true});
  channel.onopen=()=>{void(async()=>{
   const stats=await pc.getStats();
   if(generation!==this.generation)return;
   if(!hasDirectSelectedPath(stats))throw new Error('未能确认设备直接连接路径，已停止接入。');
   this.lastPong=Date.now();this.publish({connected:true,connecting:false,status:'手机已连接，请在手机上授权传感器'});this.send({type:'capabilities-request'});this.syncRun();
  })().catch(error=>{if(generation===this.generation)void this.fail(error);});};
  channel.onmessage=event=>{void this.receive(event.data,generation).catch(error=>{if(generation===this.generation)void this.fail(error);});};
  channel.onclose=()=>{if(generation===this.generation)void this.fail(new Error('手机数据通道已关闭。'));};
  channel.onerror=()=>{if(generation===this.generation)void this.fail(new Error('手机数据通道异常。'));};
  await pc.setLocalDescription(await pc.createOffer());
  if(generation===this.generation)this.signal({description:pc.localDescription});
 }
 private async remoteSignal(data:{description?:RTCSessionDescriptionInit;candidate?:RTCIceCandidateInit|null}){
  if(!this.pc||!data)throw new Error('意外的连接消息。');
  if(data.description){checkDescription(data.description,'answer');await this.pc.setRemoteDescription(data.description);for(const candidate of this.candidates)await this.pc.addIceCandidate(candidate);this.candidates=[];}
  else if(data.candidate){if(!isHostCandidate(data.candidate.candidate??''))throw new Error('拒绝中继候选。');if(this.pc.remoteDescription)await this.pc.addIceCandidate(data.candidate);else {if(this.candidates.length>=64)throw new Error('过多连接候选。');this.candidates.push(data.candidate);}}
 }
 private async tick(generation:number){
  if(generation!==this.generation)return;
  try{
   if(!this.view.connected&&Date.now()>this.deadline)throw new Error('配对超时，请重新生成二维码。');
   if(this.view.connected){if(Date.now()-this.lastPong>5000)throw new Error('手机超过 5 秒未响应，已停止连接。');this.send({type:'ping',at:Date.now()});}
   if(!this.heartbeatBusy&&this.lease){this.heartbeatBusy=true;try{await request('phone/heartbeat',{leaseId:this.lease});}finally{this.heartbeatBusy=false;}}
   if(generation===this.generation)this.publish();
  }catch(error){if(generation===this.generation)await this.fail(error);}
 }
 async bind(inputIndex:number){
  if(this.bindingBusy)return;
  const bindingGeneration=this.generation;
  this.bindingBusy=true;
  try{
   if(!this.view.connected||this.view.bound||!this.snapshot?.id||this.snapshot.status==='running')throw new Error('请先连接手机、加载实验并暂停；验证版同时绑定一个输入。');
   const input=this.snapshot.inputs?.find(i=>i.index===inputIndex);
   const kind=(input?.type==='sensor'?input.sensorType:input?.type) as PhoneKind;
   if(!hasCapability(this.view.capabilities,kind))throw new Error('请在手机上启用此传感器，并等待真实数据。');
   const owner=this.snapshot.id,generation=this.generation;
   const media=kind==='audio'||kind==='camera';
   const config={sessionId:owner,inputIndex,kind,...(kind==='audio'?{sampleRate:this.view.capabilities.audio!.sampleRate,channels:1}:{}),...(kind==='camera'?this.view.capabilities.camera:{})};
   const result=await this.local<{captureId:string;session:PhoneSnapshot}>(media?'session/phone/media/configure':'session/phone/motion/configure',config);
   if(generation!==this.generation||this.snapshot?.id!==owner)return;
   this.snapshot=result.session;this.publish({bound:{kind,inputIndex,captureId:result.captureId},error:'',status:'输入已绑定 · 返回当前实验开始测量'});this.onSession(result.session);this.runKey='';this.syncRun();
  }catch(error){if(bindingGeneration===this.generation)this.publish({error:error instanceof Error?error.message:String(error)});}
  finally{if(bindingGeneration===this.generation)this.bindingBusy=false;}
 }
 async unbind(){
  const generation=this.generation,owner=this.snapshot?.id;
  try{const result=await this.local<PhoneSnapshot>('session/phone/reset',{});if(generation!==this.generation||this.snapshot?.id!==owner)return;this.publish({bound:undefined});this.send({type:'release'});this.snapshot=result;this.onSession(result);this.syncRun();}
  catch(error){if(generation===this.generation)await this.fail(error);}
 }
 private async receive(raw:unknown,generation:number){
  if(generation!==this.generation||!this.view.connected)return;
  if(typeof raw!=='string'||new TextEncoder().encode(raw).length>65536)throw new Error('手机数据包过大或格式错误。');
  const packet=JSON.parse(raw);
  if(packet.type==='pong'){if(typeof packet.at==='number'&&Date.now()-packet.at>=0&&Date.now()-packet.at<=5000){this.lastPong=Date.now();this.view.rtt=Date.now()-packet.at;}return;}
  if(packet.type==='capabilities'){this.publish({capabilities:capabilities(packet)});return;}
  if(packet.type==='stop'){
   await this.unbind();
   if(generation===this.generation&&this.view.connected)this.publish({status:'手机已停止采集，连接保留，可重新启用传感器'});
   return;
  }
  if(packet.type==='fault')throw new Error(String(packet.message||'手机采集异常，已停止连接。'));
  if(!['motion','audio','camera'].includes(packet.type))throw new Error('未知手机数据类型。');
  const kind=packet.type==='motion'?packet.kind:packet.type;
  if(!Number.isSafeInteger(packet.sequence)||packet.sequence<0||typeof packet.runId!=='string')throw new Error('手机数据序号无效。');
  const ack=()=>this.send({type:'ack',runId:packet.runId,kind,sequence:packet.sequence});
  const bound=this.view.bound;
  if(packet.runId!==this.snapshot?.phoneRunId)return;
  if(!bound||bound.kind!==kind){ack();return;}
  if(this.inflight)throw new Error('手机输入未等待服务确认；为避免缺样已停止。');
  this.inflight=true;
  try{
   const body={...packet,sessionId:this.snapshot!.id,captureId:bound.captureId};
   const path=packet.type==='motion'?'motion/samples':packet.type==='audio'?'media/audio':'media/frame';
   const result=await this.local<{accepted?:boolean}>('session/phone/'+path,body);
   if(generation!==this.generation)return;
   if(result.accepted!==false)this.view.accepted++;
   ack();
  }catch(error){
   if(generation!==this.generation)return;
   // A pause/start may rotate the service token while an old upload is in flight.
   const now=await request<PhoneSnapshot>('session');
   if(generation!==this.generation)return;
   if(now.phoneRunId!==packet.runId){this.setSnapshot(now);this.onSession(now);return;}
   if(kind==='camera'&&(error as {status?:number}).status===429){ack();return;}
   throw error;
  }finally{if(generation===this.generation&&packet.runId===this.snapshot?.phoneRunId)this.inflight=false;}
 }
 private async fail(error:unknown){const message=error instanceof Error?error.message:String(error);await this.disconnect();this.publish({error:message,status:'连接已停止'});}
 async disconnect(){
  ++this.generation;
  if(this.timer)clearInterval(this.timer);this.timer=undefined;
  this.send({type:'release'});
  this.channel?.close();this.pc?.close();this.socket?.close();
  this.channel=undefined;this.pc=undefined;this.socket=undefined;
  this.candidates=[];this.signalQueue=Promise.resolve();this.accepting=false;this.bindingBusy=false;this.runKey='';this.inflight=false;
  const leaseId=this.lease;this.lease=undefined;
  this.publish(initialBridgeView());
  if(leaseId){try{await request('phone/release',{leaseId});}catch{/* Lease timeout also releases inputs if the page or server disappears. */}}
 }
}

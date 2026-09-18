import {useEffect,useRef,useState} from 'react';
import {request,type Snapshot} from './api';
import './BrowserSpeakerPanel.css';
type SpeakerSnapshot=Snapshot&{browserAudioOutput?:{streamId:string;outputIndex:number;sampleRate:number}[]};
type Props={snapshot:Snapshot|null;onSession:(s:Snapshot)=>void;t:(zh:string,en:string)=>string;onActivity?:(active:boolean)=>void};
type Playback={owner:string;id?:string;context:AudioContext;gain:GainNode;sequence:number;next:number;sources:Set<AudioBufferSourceNode>;timer?:ReturnType<typeof setTimeout>;watch?:ReturnType<typeof setInterval>;last:number;seen:boolean};
type Packet={streamId:string;sequence:number;sampleRate:number;channels:number;running:boolean;samples:number[]};
export default function BrowserSpeakerPanel({snapshot,onSession,t,onActivity}:Props){
 const [index,setIndex]=useState(''),[volume,setVolume]=useState(.1),[active,setActive]=useState(false),[pending,setPending]=useState(false),[error,setError]=useState('');
 const player=useRef<Playback|null>(null),latest=useRef(snapshot as SpeakerSnapshot|null),callback=useRef(onSession),activity=useRef(onActivity);latest.current=snapshot as SpeakerSnapshot|null;callback.current=onSession;activity.current=onActivity;
 useEffect(()=>{const outputs=snapshot?.outputs?.filter(o=>o.type==='audio')||[];setIndex(outputs.length===1?String(outputs[0].index):'');},[snapshot?.id]);
 function silence(p:Playback){for(const source of p.sources){try{source.stop();}catch{/* already ended */}source.disconnect();}p.sources.clear();p.next=p.context.currentTime;}
 function stop(notify=true){const p=player.current;if(!p)return;player.current=null;clearTimeout(p.timer);clearInterval(p.watch);silence(p);p.gain.disconnect();void p.context.close();setActive(false);setPending(false);activity.current?.(false);if(notify&&p.id)void request<Snapshot>('session/media/browser/speaker/stop',{sessionId:p.owner,streamId:p.id}).then(s=>{if(latest.current?.id===p.owner)callback.current(s);}).catch(()=>{});}
 useEffect(()=>{const off=()=>stop();const hidden=()=>{if(document.hidden)stop();};window.addEventListener('pagehide',off);document.addEventListener('visibilitychange',hidden);return()=>{window.removeEventListener('pagehide',off);document.removeEventListener('visibilitychange',hidden);stop();};},[]);
 useEffect(()=>{const p=player.current;if(!p)return;if(snapshot?.id!==p.owner){stop(false);return;}if(p.id&&latest.current?.browserAudioOutput?.some(x=>x.streamId===p.id))p.seen=true;else if(p.id&&p.seen){stop(false);return;}if(snapshot?.status!=='running')silence(p);},[snapshot]);
 function fail(p:Playback,message:string){if(player.current!==p)return;setError(message);stop();}
 async function poll(p:Playback){
  if(player.current!==p||!p.id)return;
  try{
   // One request in flight and at most 200 ms scheduled locally. Samples are produced by the service, never by browser formulas.
   if(p.next-p.context.currentTime>.12){p.timer=setTimeout(()=>void poll(p),30);return;}
   const sequence=p.sequence++,packet=await request<Packet>('session/media/browser/speaker/pcm',{sessionId:p.owner,streamId:p.id,sequence});
   if(player.current!==p)return;p.last=performance.now();
   if(packet.streamId!==p.id||packet.sequence!==sequence||packet.channels!==2||packet.samples.length>packet.sampleRate/5+2||packet.samples.length%2||packet.samples.some(x=>!Number.isFinite(x)||Math.abs(x)>1))throw new Error(t('后台PCM数据或序号无效。','Invalid PCM packet or sequence.'));
   if(!packet.running||latest.current?.status!=='running'){silence(p);}else if(packet.samples.length){
    if(p.context.state!=='running')throw new Error(t('浏览器已暂停音频，请重新点击启用。','Browser audio was suspended. Enable it again.'));
    const frames=packet.samples.length/2,buffer=p.context.createBuffer(2,frames,packet.sampleRate);
    for(let ch=0;ch<2;ch++){const target=buffer.getChannelData(ch);for(let i=0;i<frames;i++)target[i]=packet.samples[i*2+ch];}
    const source=p.context.createBufferSource();source.buffer=buffer;source.connect(p.gain);source.onended=()=>{p.sources.delete(source);source.disconnect();};p.sources.add(source);
    p.next=Math.max(p.next,p.context.currentTime+.02);source.start(p.next);p.next+=frames/packet.sampleRate;
   }
   p.timer=setTimeout(()=>void poll(p),packet.running?30:150);
  }catch(e){fail(p,e instanceof Error?e.message:String(e));}
 }
 async function enable(){
  if(player.current)return;setError('');const owner=latest.current?.id;if(!owner||index===''){setError(t('先加载含音频输出的实验并选择输出。','Load an audio experiment and select an output.'));return;}
  let p:Playback|undefined;
  try{
   const context=new AudioContext(),gain=context.createGain();gain.gain.value=volume;gain.connect(context.destination);
   p={owner,context,gain,sequence:0,next:0,sources:new Set(),last:performance.now(),seen:false};player.current=p;setPending(true);
   await context.resume();if(player.current!==p)return;
   const result=await request<{streamId:string;sampleRate:number;channels:number;session:Snapshot}>('session/media/browser/speaker/configure',{sessionId:owner,outputIndex:Number(index)});
   if(player.current!==p){void request('session/media/browser/speaker/stop',{sessionId:owner,streamId:result.streamId}).catch(()=>{});return;}
   p.id=result.streamId;p.last=performance.now();callback.current(result.session);setActive(true);setPending(false);activity.current?.(true);
   const current=p;p.watch=setInterval(()=>{if(performance.now()-current.last>1500)fail(current,t('扬声器与本地服务失联，声音已立即停止。','Speaker service connection lost; audio stopped.'));},200);
   void poll(p);
  }catch(e){if(p)fail(p,e instanceof Error?e.message:String(e));else setError(e instanceof Error?e.message:String(e));setPending(false);}
 }
 return <section className="panel browser-speaker"><h2>{t('扬声器','Speaker')}</h2><div className="browser-speaker-controls"><label>{t('实验音频输出','Experiment audio output')}<select value={index} disabled={active||pending} onChange={e=>setIndex(e.target.value)}><option value="">{t('请选择输出…','Choose output…')}</option>{snapshot?.outputs?.filter(x=>x.type==='audio').map(x=><option key={x.index} value={x.index}>{(x as typeof x & {name?:string}).name||t(`音频输出 ${x.index+1}`,`Audio output ${x.index+1}`)}</option>)}</select></label><label>{t('音量','Volume')} {Math.round(volume*100)}%<input aria-label={t('扬声器音量','Speaker volume')} type="range" min="0" max="1" step=".01" value={volume} onChange={e=>{const v=Number(e.target.value);setVolume(v);const p=player.current;if(p)p.gain.gain.setTargetAtTime(v,p.context.currentTime,.01);}}/></label><button className="primary" disabled={active||pending||snapshot?.status==='running'||!snapshot?.id} onClick={()=>void enable()}>{pending?t('正在启用…','Enabling…'):t('启用扬声器','Enable speaker')}</button><button disabled={!active&&!pending} onClick={()=>stop()}>{t('停止声音和实验','Stop sound & experiment')}</button></div>{active&&<p role="status">{snapshot?.status==='running'?t('播放中','Playing'):t('已启用 · 待机','Enabled · standby')}</p>}{error&&<p className="alert" role="alert">{error}</p>}<p className="muted">{t('开始实验后播放；隐藏网页会停止播放。','Playback starts with the experiment and stops when the browser page is hidden.')}</p><details><summary>{t('高级信息','Advanced information')}</summary><p className="muted">{t('使用系统默认输出，仅支持循环音频。暂停保留设置；停止、清空或切换实验后需重新启用。非循环逐分析触发暂不支持。','Uses the system default output and supports looping audio. Pause retains settings; re-enable after stop, clear or changing experiments. Non-looping per-analysis triggers are not supported.')}</p></details></section>;
}

import type {Port} from './api.ts';
/** Official graph-input-orders fixture: equal counts pair by axis order. */
export function pairGraphInputs(inputs:Port[]):{x?:Port;y:Port}[]{
 const xs=inputs.filter(p=>p.attributes.axis==='x'),ys=inputs.filter(p=>p.attributes.axis!=='x'&&p.attributes.axis!=='z');
 if(xs.length===ys.length)return ys.map((y,i)=>({x:xs[i],y}));
 let x:Port|undefined;const pairs:{x?:Port;y:Port}[]=[];
 for(const p of inputs){if(p.attributes.axis==='x')x=p;else if(p.attributes.axis!=='z')pairs.push({x,y:p});}
 return pairs;
}
export function barBounds(start:number,end:number,fraction=1):[number,number]{const inset=(end-start)*(1-fraction)/2;return [start+inset,end-inset];}
export const graphColors:Record<string,string>={orange:'#ff7e22',green:'#2bfb4c',blue:'#39a2ff',yellow:'#edf668',magenta:'#eb46f4',red:'#fe005d',white:'#ffffff',black:'#000000'};
export function paletteColor(stops:string[],fraction:number):[number,number,number]{const colors=stops.map(v=>graphColors[v]||v).map(v=>v.replace(/^#/,''));const position=Math.max(0,Math.min(1,fraction))*(colors.length-1),index=Math.floor(position),mix=position-index;const a=colors[index]||'000000',b=colors[Math.min(colors.length-1,index+1)]||a;return [0,2,4].map(i=>Math.round(parseInt(a.slice(i,i+2),16)*(1-mix)+parseInt(b.slice(i,i+2),16)*mix)) as [number,number,number];}

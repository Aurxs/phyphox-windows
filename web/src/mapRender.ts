import {paletteColor} from './graphData';
type Point={x:number;y:number;z?:number};
/** Rasterize the same alternating-row triangle grid used by Android mapXY. */
export function drawInterpolatedMap(ctx:CanvasRenderingContext2D,points:Point[],columns:number,px:(v:number)=>number,py:(v:number)=>number,z:(v:number)=>number,min:number,max:number,stops:string[],width:number,height:number){
 const layer=document.createElement('canvas');layer.width=Math.ceil(width);layer.height=Math.ceil(height);const context=layer.getContext('2d')!,data=context.createImageData(layer.width,layer.height);
 const lookup=Array.from({length:1024},(_,i)=>paletteColor(stops,i/1023));
 const mapped=points.map(p=>({x:px(p.x),y:py(p.y),z:p.z===undefined?NaN:z(p.z)}));
 function triangle(a:Point,b:Point,c:Point){if(![a.x,a.y,a.z,b.x,b.y,b.z,c.x,c.y,c.z].every(Number.isFinite))return;const denominator=(b.y-c.y)*(a.x-c.x)+(c.x-b.x)*(a.y-c.y);if(!Number.isFinite(denominator)||Math.abs(denominator)<1e-12)return;
 for(let y=Math.max(18,Math.floor(Math.min(a.y,b.y,c.y)));y<=Math.min(height-35,Math.ceil(Math.max(a.y,b.y,c.y)));y++)for(let x=Math.max(58,Math.floor(Math.min(a.x,b.x,c.x)));x<=Math.min(width-16,Math.ceil(Math.max(a.x,b.x,c.x)));x++){
 const u=((b.y-c.y)*(x+.5-c.x)+(c.x-b.x)*(y+.5-c.y))/denominator,v=((c.y-a.y)*(x+.5-c.x)+(a.x-c.x)*(y+.5-c.y))/denominator,w=1-u-v;if(u<0||v<0||w<0)continue;const rgb=lookup[Math.max(0,Math.min(1023,Math.round((u*a.z!+v*b.z!+w*c.z!-min)/(max-min||1)*1023)))],offset=(y*layer.width+x)*4;data.data[offset]=rgb[0];data.data[offset+1]=rgb[1];data.data[offset+2]=rgb[2];data.data[offset+3]=255;
 }}
 for(let i=0;i+columns+1<mapped.length;i++){if(i%columns===columns-1)continue;triangle(mapped[i],mapped[i+columns],mapped[i+1]);triangle(mapped[i+1],mapped[i+columns],mapped[i+columns+1]);}
 context.putImageData(data,0,0);ctx.drawImage(layer,0,0);
}

import type {Node} from './api.ts';
/** ExpView maps use inclusive bounds and first matching declaration. */
export function mappedText(value:number,maps:Node[]):string|undefined {
 return maps.find(n=>value>=Number(n.attributes.min??'-Infinity')&&value<=Number(n.attributes.max??'Infinity'))?.text;
}

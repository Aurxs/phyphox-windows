require 'yaml';require 'json'
keys=%w[name parent attributes children inputs outputs attribute required_component components as_required min max allows_value allows_empty repeat_offset type values required]
def compact(v,keys)
 case v
 when Hash; v.select{|k,_|keys.include?(k)}.transform_values{|x|compact(x,keys)}
 when Array;v.map{|x|compact(x,keys)}
 else v
 end
end
schemas=[]
ARGV.each do |file|
 d=YAML.load_file(file)
 d['elements'].each do |e|
  c=compact(e,keys);c['block']=d['block']
  if d['block']=='analysis' && e['parent']=='analysis'
   c['attributes']=(c['attributes']||[])+compact(d['common']['module_attributes'],keys)
  end
  if d['block']=='views' && e['parent']=='view'
   c['attributes']=(c['attributes']||[])+compact(d.fetch('common',{}).fetch('attributes',[]),keys)
  end
  schemas << c
 end
 if d['block']=='analysis'
  %w[input output].each{|tag|schemas<<{'name'=>tag,'parent'=>'@analysis-module','attributes'=>compact(d['common'][tag+'_attributes'],keys)}}
 end
end
puts JSON.pretty_generate(schemas)

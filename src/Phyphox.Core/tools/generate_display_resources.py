# SPDX-License-Identifier: GPL-3.0-only
# Extract only common_ display strings from the pinned official Android resource tree.
import xml.etree.ElementTree as E,json
from pathlib import Path
base=Path(__file__).resolve().parents[4]
resources={}
for locale,folder in [('en','values'),('zh-Hans','values-zh-rCN'),('zh-Hant','values-zh-rTW')]:
 resources[locale]={e.attrib['name'][7:]:''.join(e.itertext()).replace("\\'", "'") for e in E.parse(base/'official-reference/phyphox-android/app/src/main/res'/folder/'strings.xml').getroot() if e.attrib.get('name','').startswith('common_')}
data={'license':'GPL-3.0-only; see licenses/phyphox-android-GPL3-LICENSE.txt','source':'phyphox/phyphox-android@45fa55a0727653ccce439b86acb69c00cf435436','paths':['app/src/main/res/'+p+'/strings.xml' for p in ['values','values-zh-rCN','values-zh-rTW']],'resources':resources}
(base/'phyphox-windows/src/Phyphox.Core/DisplayResources.json').write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n')

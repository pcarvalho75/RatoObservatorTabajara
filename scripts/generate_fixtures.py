"""Freeze deterministic reference cases from the existing Python 0.1.1 engine."""
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(r'C:\Users\paulo\Desktop\Luciana\MorrisLab')
sys.path.insert(0,str(SOURCE))
import engine
import cv2

out=ROOT/'fixtures';out.mkdir(exist_ok=True)
meta=dict(width=200,height=200,fps=10,frame_count=10,duration_seconds=1)
base=engine.defaults()|dict(cx=.5,cy=.5,rx=.4,ry=.4,arena_confirmed=True)
site=base|dict(platform=dict(x=.7,y=.3,radius=.02))
cases=[]
def add(name,points,settings=base):
    cases.append(dict(name=name,metadata=meta,settings=settings,points=points,expected=engine.summarize(points,meta,settings)))
add('adjacent_displacement',[[0,80,80,'tracked',1],[1,96,80,'tracked',1]])
add('physical_units',[[0,80,80,'tracked',1],[1,96,80,'tracked',1]],base|dict(diameter_cm=100))
add('missing_does_not_bridge',[[0,80,80,'tracked',1],[1,None,None,'missing',0],[2,120,120,'tracked',1]])
add('exclusive_quadrant_boundaries',[[0,100,100,'tracked',1],[1,99,100,'tracked',1],[2,100,99,'tracked',1],[3,99,99,'tracked',1]])
add('uncertain_no_visit',[[0,80,120,'tracked',1],[1,None,None,'missing',0],[2,80,120,'tracked',1]],site)
add('uncertain_first_observed_visit',[[0,80,120,'tracked',1],[1,None,None,'missing',0],[2,140,60,'tracked',1]],site)
add('known_visit_then_gap',[[0,80,120,'tracked',1],[1,140,60,'tracked',1],[2,None,None,'missing',0]],site)
add('complete_right_censoring',[[0,80,120,'tracked',1],[1,82,122,'tracked',1]],site)
add('observed_crossings_not_invented_after_gap',[[0,120,60,'tracked',1],[1,140,60,'tracked',1],[2,None,None,'missing',0],[3,140,60,'tracked',1]],site)
add('manual_and_excluded',[[0,80,80,'tracked',1],[1,96,80,'manual',1],[2,None,None,'excluded',0]])
(out/'metric-reference-cases.json').write_text(json.dumps({'reference_version':engine.VERSION,'cases':cases},indent=2),encoding='utf-8')
reference=SOURCE/'data/b7f2c9c560d0abe7/analysis.json'
shutil.copyfile(reference,out/'python-reference-analysis.json')
ref=json.loads(reference.read_text(encoding='utf-8'))
(out/'sample-settings.json').write_text(json.dumps(ref['settings'],indent=2),encoding='utf-8')
(out/'source-manifest.json').write_text(json.dumps({'video_sha256':ref['video_sha256'],'frames':len(ref['points']),
    'reference_version':engine.VERSION,'original_video':r'C:\Users\paulo\Desktop\Luciana\Mouse.mp4',
    'development_only':True,'held_out_accuracy_validation':False},indent=2),encoding='utf-8')
print('Frozen',len(cases),'metric cases and',len(ref['points']),'original video rows.')
images=[]
for index in [170,341,512,682,853,1024,1195,1365,1536,1707,1878]:
    original=Path(r'C:\Users\paulo\Desktop\Luciana\doris-review\evidence')/f'original-{index:06d}.png'
    frame=cv2.imread(str(original));arena,_=engine.arena_pixels(ref['settings'],frame.shape[1],frame.shape[0])
    mask,_=engine.candidates(frame,ref['settings'],arena)
    shutil.copyfile(original,out/f'frame-{index:06d}.png');cv2.imwrite(str(out/f'mask-{index:06d}.png'),mask)
    images.append(dict(frame=index,input=f'frame-{index:06d}.png',expected=f'mask-{index:06d}.png'))
(out/'mask-reference-cases.json').write_text(json.dumps(images,indent=2),encoding='utf-8')
print('Frozen',len(images),'source-frame binary masks at threshold30.')

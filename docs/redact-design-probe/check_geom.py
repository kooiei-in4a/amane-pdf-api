import sys
sys.path.insert(0, __import__('os').path.dirname(__file__))
from ppm import read, px
names = {(255,0,0):"red",(0,255,0):"green",(0,0,255):"blue",(255,255,0):"yellow",(204,204,204):"gray",(0,0,0):"black",(255,255,255):"white"}
def name(c):
    best = min(names, key=lambda k: sum((a-b)**2 for a,b in zip(k,c)))
    return names[best] if sum((a-b)**2 for a,b in zip(best,c)) < 3000 else str(c)
for i in range(6):
    img = read(f"{sys.argv[1]}/g{i}.ppm"); w, h = img[0], img[1]
    tl, tr, bl, br = px(img,15,15), px(img,w-16,15), px(img,15,h-16), px(img,w-16,h-16)
    # black rect expected x in [0.4w,0.6w), y in [0.3h,0.4h)
    L, R, T, B = int(0.4*w), int(0.6*w+0.9999), int(0.3*h), int(0.4*h+0.9999)
    inside = [name(px(img,x,y)) for x,y in ((L+1,T+1),(R-2,T+1),(L+1,B-2),(R-2,B-2),((L+R)//2,(T+B)//2))]
    outside = [name(px(img,x,y)) for x,y in ((L-3,(T+B)//2),(R+2,(T+B)//2),((L+R)//2,T-3),((L+R)//2,B+2))]
    print(i, w, h, "TL", name(tl), "TR", name(tr), "BL", name(bl), "BR", name(br), "| in", set(inside), "out", set(outside))

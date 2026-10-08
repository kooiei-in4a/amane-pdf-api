#!/usr/bin/env python3
"""JPEGオプションの実動作と、合成PDFの構造・データ・描画を検証する（CI専用）。"""
import argparse
import base64
import importlib.util
import io
import json
import math
from pathlib import Path
import struct
import subprocess
import tempfile
import zlib


def command(*args):
    result = subprocess.run(args, check=True, capture_output=True, timeout=30)
    return result.stdout or result.stderr


def tool_check(directory):
    pnm = directory / 'tools.ppm'; jpeg = directory / 'tools.jpg'; decoded = directory / 'tools-decoded.ppm'
    pnm.write_bytes(b'P6\n16 16\n255\n' + bytes(16*16*3))
    print(command('djpeg', '-version').decode().strip())
    print(command('cjpeg', '-version').decode().strip())
    command('cjpeg', '-quality', '75', '-optimize', '-maxmemory', '64M', '-strict', '-outfile', str(jpeg), str(pnm))
    command('djpeg', '-scale', '3/8', '-maxmemory', '64M', '-maxscans', '100', '-strict', '-outfile', str(decoded), str(jpeg))
    assert decoded.read_bytes().startswith(b'P6\n6 6\n255\n')
    print('JPEG required options: real conversion PASS')


def pdf(objects):
    out = io.BytesIO(); out.write(b'%PDF-1.4\n'); offsets = []
    for n, value in enumerate(objects, 1):
        offsets.append(out.tell()); out.write(f'{n} 0 obj\n'.encode() + value + b'\nendobj\n')
    pos = out.tell(); out.write(f'xref\n0 {len(objects)+1}\n0000000000 65535 f \n'.encode())
    for offset in offsets: out.write(f'{offset:010} 00000 n \n'.encode())
    out.write(f'trailer\n<< /Size {len(objects)+1} /Root 1 0 R >>\nstartxref\n{pos}\n%%EOF\n'.encode())
    return out.getvalue()


def stream(dictionary, data):
    return f'<< {dictionary} /Length {len(data)} >>\nstream\n'.encode() + data + b'\nendstream'


def image_pdf(raw, width, height, color='/DeviceRGB', filter='/DCTDecode', extra='', extras=(), form=False, inline=False):
    content = b'q 500 0 0 350 40 30 cm /Im0 Do Q\n0 0 1 RG 20 600 200 80 re S\nBT /F1 18 Tf 20 730 Td (PDF text and vectors) Tj ET\n'
    dictionary = f'/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {color} /BitsPerComponent 8 /Filter {filter} {extra}'
    objects = [b'<< /Type /Catalog /Pages 2 0 R >>', b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
        b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im0 4 0 R >> /Font << /F1 6 0 R >> >> /Contents 5 0 R >>',
        stream(dictionary, raw), stream('', content), b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>', *extras]
    if form:
        objects.append(objects[3]); ref = len(objects)
        objects[3] = stream(f'/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Inner {ref} 0 R >> >>', b'/Inner Do\n')
    if inline:
        objects[3] = b'null'
        content = content.replace(b'/Im0 Do', f'BI /W {width} /H {height} /CS {color} /BPC 8 /F {filter} ID\n'.encode() + raw + b'\nEI')
        objects[4] = stream('', content)
        objects[2] = objects[2].replace(b'/Im0 4 0 R', b'')
    return pdf(objects)


def inspect(path, level='none'):
    return json.loads(command('qpdf', '--json=2', '--json-stream-data=inline', '--decode-level='+level, str(path)))


def render(path, directory, label):
    from PIL import Image
    import numpy as np
    prefix = directory / label
    completed = subprocess.run(['pdftoppm','-r','72','-singlefile','-png',str(path),str(prefix)],capture_output=True,timeout=30,check=True)
    assert not completed.stderr, 'Renderer reported a malformed fixture or output: ' + completed.stderr.decode()
    return np.asarray(Image.open(str(prefix)+'.png'), dtype=np.float32)


def lzw(data):
    # Clear every 100 literal codes; the code width remains nine bits.
    codes = []
    for n in range(0,len(data),100): codes += [256,*data[n:n+100]]
    codes.append(257); bits = ''.join(f'{code:09b}' for code in codes)
    bits += '0' * (-len(bits)%8)
    return bytes(int(bits[n:n+8],2) for n in range(0,len(bits),8))


def excluded_streams(document):
    objects = document['qpdf'][1]
    return {obj['stream']['data'] for obj in objects.values() if 'stream' in obj and obj['stream']['dict'].get('/Subtype') == '/Image'}


def render_check(image, directory):
    from PIL import Image, ImageCms
    import numpy as np
    spec=importlib.util.spec_from_file_location('smoke',Path(__file__).with_name('docker-smoke.py'))
    smoke=importlib.util.module_from_spec(spec); spec.loader.exec_module(smoke)
    w,h=2200,1600
    x=np.arange(w)[None,:]; y=np.arange(h)[:,None]
    rng=np.random.default_rng(22)
    gray=np.clip(60+(x//12+y//17)%150+rng.integers(0,35,(h,w)),0,255).astype('uint8')
    rgb=np.stack((gray,np.roll(gray,17,axis=1),255-gray),axis=2)
    def jpeg(array,quality=90):
        out=io.BytesIO(); Image.fromarray(array).save(out,format='JPEG',quality=quality); return out.getvalue()
    color_jpeg=jpeg(rgb); gray_jpeg=jpeg(gray)
    profile=ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes()
    soft=stream(f'/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode',zlib.compress(bytes([180])*(w*h)))
    mask=stream(f'/Type /XObject /Subtype /Image /Width {w} /Height {h} /ImageMask true /BitsPerComponent 1 /Filter /FlateDecode',zlib.compress(bytes([0x55])*(((w+7)//8)*h)))
    cases=[('rgb',image_pdf(color_jpeg,w,h),1),('gray',image_pdf(gray_jpeg,w,h,color='/DeviceGray'),1),
        ('icc',image_pdf(color_jpeg,w,h,color='[/ICCBased 7 0 R]',extras=[stream('/N 3 /Alternate /DeviceRGB',profile)]),1),
        ('calrgb',image_pdf(color_jpeg,w,h,color='[/CalRGB << /WhitePoint [0.9505 1 1.0890] >>]'),1),
        ('calgray',image_pdf(gray_jpeg,w,h,color='[/CalGray << /WhitePoint [0.9505 1 1.0890] >>]'),1),
        ('smask',image_pdf(color_jpeg,w,h,extra='/SMask 7 0 R /Interpolate true',extras=[soft]),1),
        ('mask',image_pdf(color_jpeg,w,h,extra='/Mask 7 0 R',extras=[mask]),1),
        ('matte',image_pdf(color_jpeg,w,h,extra='/SMask 7 0 R',extras=[soft.replace(b'/Type /XObject',b'/Matte [0 0 0] /Type /XObject')]),0),
        ('decode',image_pdf(color_jpeg,w,h,extra='/Decode [0 1 0 1 0 1]'),0),
        ('colorkey',image_pdf(color_jpeg,w,h,extra='/Mask [0 0 0 0 0 0]'),0),
        ('form',image_pdf(color_jpeg,w,h,form=True),0),('inline',image_pdf(color_jpeg,w,h,inline=True),0)]
    data=bytes((x+y)%256 for y in range(32) for x in range(32))
    rle=b''.join(bytes([len(data[n:n+128])-1])+data[n:n+128] for n in range(0,len(data),128))+b'\x80'
    filters=[('flate','/FlateDecode',zlib.compress(data)),('runlength','/RunLengthDecode',rle),
        ('lzw','/LZWDecode',lzw(data)),('hex','/ASCIIHexDecode',data.hex().encode()+b'>'),
        ('a85','/ASCII85Decode',base64.a85encode(data)+b'~>'),
        ('compound','[/ASCII85Decode /FlateDecode]',base64.a85encode(zlib.compress(data))+b'~>')]
    cases += [(name,image_pdf(raw,32,32,color='/DeviceGray',filter=filter),0) for name,filter,raw in filters]
    jpx=io.BytesIO(); Image.frombytes('L',(32,32),data).save(jpx,format='JPEG2000')
    cases.append(('jpx',image_pdf(jpx.getvalue(),32,32,color='/DeviceGray',filter='/JPXDecode'),0))
    bw=Image.frombytes('L',(32,32),data).convert('1'); tiff=io.BytesIO(); bw.save(tiff,format='TIFF',compression='group4')
    tiff.seek(0); tagged=Image.open(tiff); offset=tagged.tag_v2[273][0]; size=tagged.tag_v2[279][0]; mmr=tiff.getvalue()[offset:offset+size]
    ccitt=image_pdf(mmr,32,32,color='/DeviceGray',filter='/CCITTFaxDecode',extra='/DecodeParms << /K -1 /Columns 32 /Rows 32 >>')
    ccitt=ccitt.replace(b'/BitsPerComponent 8',b'/BitsPerComponent 1')
    cases.append(('ccitt',ccitt,0))
    # Embedded JBIG2: page info and one immediate generic MMR region, generated
    # from the same Group 4 bitmap. No file header or external encoder is needed.
    def segment(number,type,data): return struct.pack('>IBBBI',number,type,0,1,len(data))+data
    jbig2=segment(1,48,struct.pack('>IIIIBH',32,32,0,0,0,0))+segment(2,38,struct.pack('>IIIIBB',32,32,0,0,0,1)+mmr)+segment(3,51,b'')
    cases.append(('jbig2',image_pdf(jbig2,32,32,color='/DeviceGray',filter='/JBIG2Decode').replace(b'/BitsPerComponent 8',b'/BitsPerComponent 1'),0))
    cmyk=io.BytesIO(); Image.fromarray(rgb).convert('CMYK').save(cmyk,format='JPEG',quality=90)
    cases.append(('cmyk',image_pdf(cmyk.getvalue(),w,h,color='/DeviceCMYK'),0))
    cases.append(('lab',image_pdf(color_jpeg,w,h,color='[/Lab << /WhitePoint [0.9505 1 1.0890] >>]'),0))
    cases.append(('indexed',image_pdf(zlib.compress(data),32,32,color='[/Indexed /DeviceRGB 255 <'+''.join(f'{n:02x}'*3 for n in range(256))+'>]',filter='/FlateDecode'),0))
    tint='<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 0] /N 1 >>'
    cases.append(('separation',image_pdf(gray_jpeg,w,h,color='[/Separation /Spot /DeviceRGB '+tint+']'),0))
    cases.append(('devicen',image_pdf(gray_jpeg,w,h,color='[/DeviceN [/Spot] /DeviceRGB '+tint+']'),0))
    with smoke.running_container(image,isolated=True) as api:
        for name,source,count in cases:
            before=directory/(name+'-input.pdf'); before.write_bytes(source)
            command('qpdf','--check',str(before))
            original=inspect(before); a=render(before,directory,name+'-a')
            for level in ('standard','strong'):
                body,ct=smoke.multipart(source,password=None)
                code,headers,result=api.request('POST','/api/pdf/compress?level='+level,body,ct)
                assert code==200,(name,level,code)
                assert int(headers['X-Pdf-Images-Recompressed'])==count,(name,level,headers)
                after=directory/(name+'-'+level+'.pdf'); after.write_bytes(result)
                command('qpdf','--check',str(after)); output=inspect(after)
                assert len(original['pages'])==len(output['pages'])==1
                for doc in (original,output):
                    page=doc['qpdf'][1]['obj:'+doc['pages'][0]['object']]['value']
                    assert page['/MediaBox']==[0,0,595,842]
                if count==0:
                    mode='generalized' if name in ('lzw','hex','a85','compound') else 'none'
                    assert excluded_streams(inspect(before,mode))==excluded_streams(inspect(after,mode)),(name,'data mismatch')
                elif name in ('icc','mask','smask'):
                    # Profiles and retained masks remain byte-identical (object IDs may change).
                    source_other={obj['stream']['data'] for obj in inspect(before,'generalized')['qpdf'][1].values() if 'stream' in obj and obj['stream']['data']!=base64.b64encode(color_jpeg).decode()}
                    output_all={obj['stream']['data'] for obj in inspect(after,'generalized')['qpdf'][1].values() if 'stream' in obj}
                    # Page content may be losslessly Flate encoded by qpdf.
                    for value in source_other:
                        if value!=original['qpdf'][1]['obj:5 0 R']['stream']['data']: assert value in output_all,(name,'mask/profile mismatch')
                b=render(after,directory,name+'-'+level+'-b'); assert a.shape==b.shape
                assert np.array_equal(a[:400],b[:400]),(name,'text/vector rendering mismatch')
                if count==0: assert np.array_equal(a,b),(name,'excluded rendering mismatch')
                else:
                    mse=float(np.mean((a-b)**2)); psnr=10*math.log10(255**2/mse) if mse else math.inf
                    assert psnr>22,(name,level,psnr)
                api.assert_clean()
            print('structure/data/render',name,'both levels PASS',flush=True)
    print('CI-only rendering comparisons PASS')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tools',action='store_true'); parser.add_argument('--render',metavar='IMAGE')
    args=parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='amane-compress-validation-') as tmp:
        root=Path(tmp)
        if args.tools: tool_check(root)
        if args.render: render_check(args.render,root)


if __name__=='__main__': main()

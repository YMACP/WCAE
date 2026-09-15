"""Prepare WCAE build inputs outside the source tree. End users only need WCAE.exe."""
import urllib.request,json,hashlib,pathlib,zipfile,sys,os,argparse,shutil
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--build-cache',default=str(pathlib.Path(os.environ['LOCALAPPDATA'])/'WCAE-build'))
parser.add_argument('--wkhtmltopdf',type=pathlib.Path,help='Path to wkhtmltopdf 0.12.6 x64 executable, only needed on a new build machine.')
parser.add_argument('--ffmpeg-only',action='store_true')
args=parser.parse_args()
P=pathlib.Path(args.build_cache).resolve()
source=pathlib.Path(__file__).resolve().parent
if P==source or source in P.parents:raise SystemExit('Build cache must be outside the source directory.')
T=P/'dependencies'/'tools';T.mkdir(parents=True,exist_ok=True)
def get(url):
    return urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'WCAE-build'}),timeout=90)
def download(url,path):
    temp=path.with_suffix(path.suffix+'.download')
    with get(url) as r,temp.open('wb') as f:
        while True:
            b=r.read(65536)
            if not b:break
            f.write(b)
    temp.replace(path)
release=json.load(get('https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest'))
assets={a['name']:a['browser_download_url'] for a in release['assets']}
checks=get(assets['SHA2-256SUMS']).read().decode()
if not args.ffmpeg_only:download(assets['yt-dlp.exe'],T/'yt-dlp.exe')
expected=next(line.split()[0] for line in checks.splitlines() if line.split()[-1].lstrip('*')=='yt-dlp.exe')
actual=hashlib.sha256((T/'yt-dlp.exe').read_bytes()).hexdigest()
assert actual==expected,(actual,expected)
print('yt-dlp verified',release['tag_name'],flush=True)
ff_release=json.load(get('https://api.github.com/repos/GyanD/codexffmpeg/releases/latest'))
ff_asset=next(a for a in ff_release['assets'] if a['name'].endswith('-essentials_build.zip'))
url=ff_asset['browser_download_url']
zip_path=P/'obj'/'ffmpeg-essentials.zip';zip_path.parent.mkdir(exist_ok=True)
download(url,zip_path)
checksum=ff_asset['digest'].split(':',1)[1]
actual=hashlib.sha256(zip_path.read_bytes()).hexdigest()
assert actual==checksum,(actual,checksum)
with zipfile.ZipFile(zip_path) as z:
    for filename in ['ffmpeg.exe','ffprobe.exe']:
        member=next(n for n in z.namelist() if n.endswith('/bin/'+filename))
        (T/filename).write_bytes(z.read(member))
    for name in z.namelist():
        if name.endswith('/LICENSE'): (T/'ffmpeg-LICENSE.txt').write_bytes(z.read(name))
(T/'UPSTREAM.json').write_text(json.dumps({'yt-dlp':{'version':release['tag_name'],'url':assets['yt-dlp.exe'],'sha256':expected},'ffmpeg':{'url':url,'archive_sha256':checksum}},indent=2),encoding='utf-8')
download('https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/LICENSE',T/'yt-dlp-LICENSE.txt')
print('ffmpeg and ffprobe verified and extracted',flush=True)
if args.wkhtmltopdf:
    shutil.copy2(args.wkhtmltopdf,T/'wkhtmltopdf.exe')
if not (T/'wkhtmltopdf.exe').is_file():
    raise SystemExit('For a fresh developer build, supply --wkhtmltopdf <wkhtmltopdf-0.12.6-x64.exe>. Existing build cache already contains it.')
for name in ('MSVCP140.dll','VCRUNTIME140.dll','VCRUNTIME140_1.dll'):
    candidate=pathlib.Path(os.environ['WINDIR'])/'System32'/name
    if candidate.is_file():shutil.copy2(candidate,T/name)
print('WCAE build tools: '+str(T),flush=True)

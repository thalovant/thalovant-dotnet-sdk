"""Reproduce the portable managed Noise primitive subset from the pinned upstream checkout.
Usage: python3 tools/vendor-noise-primitives.py /path/to/bc-csharp
Compatibility.cs is a separately reviewed non-cryptographic adapter and is preserved.
"""
from pathlib import Path
import re, subprocess, sys
checkout = Path(sys.argv[1]).resolve()
revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=checkout, text=True).strip()
if revision != "b4f2f6ad76bcd1f11f365ee50cc7447fbce79077":
    raise SystemExit("Expected Bouncy Castle 2.6.2 commit b4f2f6ad76bcd1f11f365ee50cc7447fbce79077")
root=checkout / 'crypto/src'
out=Path(__file__).resolve().parents[1] / 'src/Thalovant.Sdk/Internal/BouncyCastle'
out.mkdir(parents=True,exist_ok=True)
def portable(text):
    stack=[];active=True;result=[]
    for line in text.splitlines():
        if line.strip().startswith('#if'):
            stack.append(active);active=False
        elif line.strip().startswith('#else'): active=stack[-1] and not active
        elif line.strip().startswith('#elif'): active=False
        elif line.strip().startswith('#endif'): active=stack.pop()
        elif active: result.append(line)
    return '\n'.join(result)+'\n'
def method_span(text,name):
    match=re.search(r'        (?:public|private|internal) (?:static )?[^\n]*\b'+name+r'\([^;]*?\)\s*\{',text)
    if not match:return None
    start=match.start();brace=text.index('{',match.start());depth=1;i=brace+1
    while depth:
        depth+=(text[i]=='{')-(text[i]=='}');i+=1
    return start,i

def remove(text,name):
    while (span:=method_span(text,name)):text=text[:span[0]]+text[span[1]:]
    return text
sources=['crypto/generators/Argon2BytesGenerator.cs','crypto/digests/Blake2bDigest.cs','crypto/parameters/Argon2Parameters.cs','crypto/IDigest.cs','crypto/ICharToByteConverter.cs','math/ec/rfc7748/X25519.cs','math/ec/rfc7748/X25519Field.cs','math/raw/Mod.cs']
for f in sources:
    s=portable((root/f).read_text(encoding='utf-8-sig'))
    if f.endswith('X25519.cs'):
        s=remove(remove(s,'GeneratePrivateKey'),'Precompute')
        span=method_span(s,'ScalarMultBase');s=s[:span[0]]+'''        public static void ScalarMultBase(byte[] k, int kOff, byte[] r, int rOff)
        {
            // The equivalent Montgomery basepoint path documented by upstream;
            // avoids importing the unrelated Ed25519 implementation.
            byte[] u = new byte[PointSize]; u[0] = 9;
            ScalarMult(k, kOff, u, 0, r, rOff);
        }'''+s[span[1]:]
        s=s.replace('using Org.BouncyCastle.Math.EC.Rfc8032;','').replace('using Org.BouncyCastle.Security;','')
    if f.endswith('Mod.cs'):
        s=remove(s,'Random').replace('using Org.BouncyCastle.Security;','').replace('using Org.BouncyCastle.Crypto.Utilities;','')
    s=s.replace('Org.BouncyCastle','Thalovant.Internal.BouncyCastle')
    s=re.sub(r'    public (sealed |static )?(class|interface)',r'    internal \1\2',s)
    (out/Path(f).name).write_text('// Derived from Bouncy Castle 2.6.2; see THIRD-PARTY-NOTICES.md.\n#nullable disable\n'+s)
# Retain only used portable helpers verbatim from the pinned source.
for f,names in [('math/raw/Nat.cs',['Xor64','XorTo64','XorBothTo64','GetBitLength','Gte','LessThan']),('util/Integers.cs',['NumberOfLeadingZeros','NumberOfTrailingZeros']),('util/Longs.cs',['RotateRight']),('crypto/util/Pack.cs',['LE_To_UInt32','LE_To_UInt64','UInt64_To_LE','UInt32_To_LE'])]:
    if not (root/f).exists(): print('missing',f);continue
    src=portable((root/f).read_text(encoding='utf-8-sig')); methods=[]
    for name in names:
        while(span:=method_span(src,name)):
            methods.append(src[span[0]:span[1]]);src=src[:span[0]]+src[span[1]:]
    namespace='Math.Raw' if 'math/raw' in f else 'Crypto.Utilities' if 'crypto/util/' in f else 'Utilities'
    cname=Path(f).stem
    body='\n\n'.join(methods)
    (out/Path(f).name).write_text('// Extracted portable helpers from Bouncy Castle 2.6.2; see THIRD-PARTY-NOTICES.md.\n#nullable disable\nusing System;\nusing System.Diagnostics;\nusing Thalovant.Internal.BouncyCastle.Utilities;\nnamespace Thalovant.Internal.BouncyCastle.'+namespace+' {\ninternal static class '+cname+' {\n'+body+'\n}}\n')

# Remove irrelevant assembly/optimized-allocation metadata after portable extraction.
p = out / "X25519Field.cs"
p.write_text(p.read_text().replace("[CLSCompliant(false)]", ""))
p = out / "Mod.cs"
p.write_text(p.read_text().replace("        private static readonly int MaxStackAlloc = Platform.Is64BitProcess ? 4096 : 1024;", ""))
p = out / "Integers.cs"
upstream = (root / "util/Integers.cs").read_text(encoding="utf-8-sig")
table = re.search(r"        private static readonly byte\[\] DeBruijnTZ = \{.*?\};", upstream, re.S).group(0)
p.write_text(p.read_text().replace("internal static class Integers {", "internal static class Integers {\n" + table + "\n"))

# Normalize whitespace without changing the retained algorithms.
for p in out.glob("*.cs"):
    p.write_text("\n".join(line.rstrip() for line in p.read_text().splitlines()) + "\n")

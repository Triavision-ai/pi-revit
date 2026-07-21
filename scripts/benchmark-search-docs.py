# -*- coding: utf-8 -*-
"""Reproducible benchmark of pi-revit search_api_docs against RevitAPI.xml ground truth.

Usage: python scripts/benchmark-search-docs.py [results.json] [path-to-RevitAPI.xml]
Requires: Revit running with the pi-revit bridge add-in loaded (a document need not
be open) and Python 3.8+. Ground truth is Autodesk's own RevitAPI.xml, so every
number is reproducible on any machine with the same Revit version.

Categories:
  A exact-name recall/ranking     B signature + spacing variants
  C complex syntax (arrays, nested generics, ref/out, ctors)
  D namespace ambiguity           E honesty/adversarial controls
  F documentation fidelity        G latency stats
"""
import glob, json, os, random, re, sys, time, urllib.request, xml.etree.ElementTree as ET
from collections import defaultdict

random.seed(20260722)  # reproducible sampling

APPDATA = os.environ.get("APPDATA") or os.path.expanduser(r"~\AppData\Roaming")
BRIDGE = json.load(open(os.path.join(APPDATA, "RevitBridge", "bridge.json")))
BASE, TOKEN = BRIDGE["baseUrl"], BRIDGE["token"]
if len(sys.argv) > 2:
    XML_PATH = sys.argv[2]
else:
    revit_version = BRIDGE.get("revitVersion", "")
    candidates = glob.glob(rf"C:\Program Files\Autodesk\Revit {revit_version}*\RevitAPI.xml") \
        or glob.glob(r"C:\Program Files\Autodesk\Revit 20*\RevitAPI.xml")
    if not candidates:
        sys.exit("RevitAPI.xml not found; pass its path as the second argument.")
    XML_PATH = candidates[-1]

LAT = []
def search(query, max_results=10):
    body = json.dumps({"query": query, "max_results": max_results}).encode("utf-8")
    url = f"{BASE}/tools/search_api_docs/execute?token={TOKEN}&timeout_ms=60000"
    req = urllib.request.Request(url, data=body, headers={"content-type": "application/json"}, method="POST")
    t0 = time.perf_counter()
    with urllib.request.urlopen(req, timeout=90) as resp:
        payload = json.load(resp)
    LAT.append((time.perf_counter() - t0) * 1000)
    det = (payload.get("details") or {}).get("payload") or {}
    return det.get("matches") or [], det.get("totalMatches", 0)

# ---------------------------------------------------------------- ground truth
PRIM = {"System.String": "string", "System.Boolean": "bool", "System.Double": "double",
        "System.Int32": "int", "System.Int64": "long", "System.Object": "object",
        "System.Byte": "byte", "System.Single": "float", "System.UInt32": "uint"}

def split_top(params):  # split on commas outside {}
    out, depth, cur = [], 0, ""
    for ch in params:
        if ch == "{": depth += 1
        elif ch == "}": depth -= 1
        if ch == "," and depth == 0: out.append(cur); cur = ""
        else: cur += ch
    if cur: out.append(cur)
    return out

def short_type(t):
    t = t.strip()
    byref = t.endswith("@")
    if byref: t = t[:-1]
    array = ""
    while t.endswith("[]"): array += "[]"; t = t[:-2]
    if t in PRIM: base = PRIM[t]
    elif "{" in t:
        outer, inner = t[:t.index("{")], t[t.index("{")+1:t.rindex("}")]
        outer = outer.split(".")[-1].split("`")[0]
        base = f"{outer}<{', '.join(short_type(x) for x in split_top(inner))}>"
    else:
        base = t.split(".")[-1]
    return base + array + ("&" if byref else "")

class Member:
    __slots__ = ("kind","full","typename","member","params","summary","pnames","since")
    def __init__(s, kind, full, typename, member, params, summary, pnames, since):
        s.kind, s.full, s.typename, s.member = kind, full, typename, member
        s.params, s.summary, s.pnames, s.since = params, summary, pnames, since
    @property
    def short_sig_prefix(s):
        shorts = [short_type(p) for p in s.params]
        head = s.typename if s.member == "#ctor" else f"{s.typename}.{s.member}"
        return f"{head}(" + ", ".join(shorts)

def text_of(el):
    if el is None: return None
    return re.sub(r"\s+", " ", "".join(el.itertext())).strip() or None

print("parsing RevitAPI.xml ...", flush=True)
root = ET.parse(XML_PATH).getroot()
methods, props, types = [], [], []
for m in root.iter("member"):
    name = m.get("name", "")
    summary = text_of(m.find("summary"))
    since = text_of(m.find("since"))
    pnames = [p.get("name") for p in m.findall("param")]
    if name.startswith("M:"):
        body = name[2:]
        params = ""
        if "(" in body:
            body, params = body[:body.index("(")], body[body.index("(")+1:body.rindex(")")]
        parts = body.split(".")
        if len(parts) < 2: continue
        member, typename = parts[-1], parts[-2]
        if "`" in typename or "`" in member: continue
        full = ".".join(parts)
        methods.append(Member("M", full, typename, member, split_top(params) if params else [], summary, pnames, since))
    elif name.startswith("P:"):
        parts = name[2:].split(".")
        if len(parts) < 2 or "`" in parts[-2]: continue
        props.append(Member("P", name[2:], parts[-2], parts[-1], [], summary, pnames, since))
    elif name.startswith("T:"):
        parts = name[2:].split(".")
        if "`" in parts[-1]: continue
        types.append(Member("T", name[2:], parts[-1], "", [], summary, pnames, since))

print(f"golden pool: {len(methods)} methods, {len(props)} properties, {len(types)} types", flush=True)

by_ns = defaultdict(list)
for mm in methods: by_ns[".".join(mm.full.split(".")[:-2])].append(mm)
real_methods = {(m.typename, m.member) for m in methods}
overload_count = defaultdict(int)
for m in methods: overload_count[(m.typename, m.member)] += 1

def stratified(pool, n):
    groups = defaultdict(list)
    for m in pool: groups[".".join(m.full.split(".")[:4])].append(m)
    out, keys = [], sorted(groups)
    while len(out) < n and keys:
        for k in list(keys):
            if groups[k]: out.append(groups[k].pop(random.randrange(len(groups[k]))))
            else: keys.remove(k)
            if len(out) >= n: break
    return out

R = {}  # results

# ---------------------------------------------------------- A: exact-name recall
A = {"n":0,"top1":0,"top3":0,"recall":0}
sampleA = stratified(methods, 90) + stratified(props, 40) + stratified(types, 30)
for m in sampleA:
    q = f"{m.typename}.{m.member}" if m.kind in "MP" else m.typename
    matches, _ = search(q)
    target = m.full if m.kind in "MP" else m.full
    names = [x.get("name","") for x in matches]
    A["n"] += 1
    A["recall"] += any(n == target for n in names)
    A["top1"] += bool(names and names[0] == target)
    A["top3"] += any(n == target for n in names[:3])
print("A done", A, flush=True)
R["A exact-name"] = A

# ------------------------------------------------- B: signatures + spacing variants
B = {"n":0,"top1_sig":0,"variants_agree":0,"variant_sets":0}
sigpool = [m for m in methods if 2 <= len(m.params) <= 5]
sampleB = stratified(sigpool, 60)
for m in sampleB:
    try: prefix = m.short_sig_prefix
    except Exception: continue
    canonical = prefix  # "Type.Member(P1, P2..."
    matches, _ = search(canonical)
    B["n"] += 1
    sigs = [x.get("signature","") for x in matches]
    want = f"new {m.typename}(" if m.member == "#ctor" else f"{m.typename}.{m.member}("
    ok = bool(sigs and sigs[0].startswith(want))
    B["top1_sig"] += ok
    variants = [canonical.replace(", ", ","),
                canonical.replace(", ", " , "),
                canonical.replace(", ", ",  "),
                canonical.replace("(", "( ", 1)]
    counts = {len(matches)}
    for v in variants:
        vm, _ = search(v)
        counts.add(len(vm))
    B["variant_sets"] += 1
    B["variants_agree"] += (len(counts) == 1)
print("B done", B, flush=True)
R["B signature+spacing"] = B

# --------------------------------------------------------- C: complex syntax shapes
C = {"cases": []}
def probe(label, q, expect_nonzero=True):
    try: matches, total = search(q)
    except Exception as e:
        C["cases"].append((label, q, f"ERROR {e}")); return
    ok = (total > 0) if expect_nonzero else (total == 0)
    top = matches[0]["signature"] if matches else "-"
    C["cases"].append((label, q, f"{'OK' if ok else 'MISS'} total={total} top={top[:70]}"))

arr = next((m for m in methods if any(p.endswith("[]") for p in m.params)), None)
if arr: probe("array param", arr.short_sig_prefix)
nest = next((m for m in methods if any("{" in p and "{" in p[p.index("{")+1:] for p in m.params)), None)
if nest: probe("nested generic", nest.short_sig_prefix)
byref = next((m for m in methods if any(p.endswith("@") for p in m.params)), None)
if byref: probe("ref/out param", byref.short_sig_prefix)
ctor = next((m for m in methods if m.member == "#ctor" and m.typename == "FilteredElementCollector"), None)
probe("constructor", "FilteredElementCollector(Document")
probe("property", "PDFExportOptions.FileName")
probe("fully qualified type", "Autodesk.Revit.DB.Document")
probe("case-insensitive", "wall.create(document, curve")
probe("Double vs double", "UnitUtils.ConvertToInternalUnits(Double, ForgeTypeId")
probe("trailing garbage brackets", "Wall.Create(Document, Curve]]", True)  # observational
probe("double periods", "Wall..Create", True)  # observational
print("C done", flush=True)
R["C complex-syntax"] = C

# ------------------------------------------------------------ D: namespace ambiguity
D = {"cases": []}
def ns_case(q, want_substr):
    matches, total = search(q)
    top = matches[0]["name"] if matches else "-"
    D["cases"].append((q, want_substr, top, "OK" if want_substr in top else "MISS"))
for q, want in [
    ("Autodesk.Revit.DB.Architecture.Room", ".Architecture.Room"),
    ("Autodesk.Revit.DB.Mechanical.Space", ".Mechanical.Space"),
    ("Architecture.Room", ".Architecture.Room"),
    ("Mechanical.Duct", ".Mechanical.Duct"),
    ("Plumbing.Pipe", ".Plumbing.Pipe"),
    ("Electrical.Wire", ".Electrical.Wire"),
    ("Structure.Rebar", ".Structure.Rebar"),
    ("Room", "Room"), ("Space", "Space"), ("Area", "Area"),
    ("Connector", "Connector"),
]: ns_case(q, want)
print("D done", flush=True)
R["D namespace"] = D

# ------------------------------------------------------- E: honesty / adversarial
E = {"n":0,"exact_fp":0,"zero":0,"kinds":defaultdict(lambda:[0,0,0])}
adv = []
pool = stratified([m for m in methods if 1 <= len(m.params) <= 4 and m.member != "#ctor"], 120)
for m in pool[:30]:  # misspelled member (drop 2nd letter)
    if len(m.member) < 5: continue
    adv.append(("misspelled-member", f"{m.typename}.{m.member[0]+m.member[2:]}", m))
for m in pool[30:60]:  # nonsense parameter
    adv.append(("nonsense-param", f"{m.typename}.{m.member}(Banana", m))
for m in pool[60:90]:  # wrong declaring class
    other = random.choice(types).typename
    if (other, m.member) in real_methods: continue
    adv.append(("wrong-class", f"{other}.{m.member}", (other, m.member)))
for m in pool[90:120]:  # swapped params (only if that order is not a real overload)
    if len(m.params) < 2 or m.params[0] == m.params[1]: continue
    sw = [m.params[1], m.params[0]] + m.params[2:]
    exists = any(o.params[:len(sw)] == sw for o in methods if (o.typename,o.member)==(m.typename,m.member))
    if exists: continue
    shorts = [short_type(p) for p in sw]
    adv.append(("swapped-params", f"{m.typename}.{m.member}(" + ", ".join(shorts), m))
for kind, q, _meta in adv:
    matches, total = search(q)
    E["n"] += 1
    zero = (total == 0)
    # exact false positive: a returned entry CLAIMS the impossible thing exists
    fp = False
    if kind == "wrong-class":
        t, mem = _meta
        fp = any(x.get("name","").endswith(f".{t}.{mem}") or f".{t}." in x.get("name","") and x.get("name","").endswith(f".{mem}") for x in matches)
    elif kind == "swapped-params":
        fp = any(x.get("signature","").lower() == (q.lower()+")") for x in matches)
    elif kind == "nonsense-param":
        fp = any("banana" in x.get("signature","").lower() for x in matches)
    elif kind == "misspelled-member":
        bad = q.split(".")[-1].lower()
        fp = any(x.get("name","").lower().endswith("."+bad) for x in matches)
    E["exact_fp"] += fp
    E["zero"] += zero
    k = E["kinds"][kind]; k[0] += 1; k[1] += fp; k[2] += zero
print("E done", {k: list(v) for k, v in E["kinds"].items()}, flush=True)
E["kinds"] = {k: list(v) for k, v in E["kinds"].items()}
R["E honesty"] = E

# ------------------------------------------------------------- F: doc fidelity
F = {"n":0,"param_names_ok":0,"summary_ok":0,"since_ok":0,"since_present":0,"overload_docs_ok":0,"overload_checked":0}
fsample = stratified([m for m in methods if m.pnames and m.summary and m.member != "#ctor"], 50)
for m in fsample:
    try: q = m.short_sig_prefix
    except Exception: continue
    matches, _ = search(q)
    if not matches: continue
    top = matches[0]
    F["n"] += 1
    got_pnames = [p.get("name") for p in (top.get("parameters") or [])]
    F["param_names_ok"] += (got_pnames == m.pnames)
    gs, ws = (top.get("summary") or ""), m.summary
    F["summary_ok"] += (re.sub(r"\W","",gs)[:80] == re.sub(r"\W","",ws)[:80])
    if m.since:
        F["since_present"] += 1
        F["since_ok"] += (top.get("since") == m.since)
    # overload-doc attribution: right param COUNT on the top signature
    if overload_count[(m.typename, m.member)] > 1:
        F["overload_checked"] += 1
        sig = top.get("signature","")
        inner = sig[sig.index("(")+1:sig.rindex(")")] if "(" in sig else ""
        F["overload_docs_ok"] += (len(split_top(inner)) == len(m.params) if inner else len(m.params)==0)
print("F done", F, flush=True)
R["F fidelity"] = F

# ------------------------------------------------------------------ G: latency
LAT.sort()
def pct(p): return LAT[min(len(LAT)-1, int(len(LAT)*p))]
R["G latency"] = {"queries": len(LAT), "cold_first_ms": round(LAT[-1] if not LAT else None or 0),
                  "p50_ms": round(pct(0.50)), "p95_ms": round(pct(0.95)), "p99_ms": round(pct(0.99)),
                  "max_ms": round(LAT[-1])}

json.dump(R, open(sys.argv[1] if len(sys.argv)>1 else "benchmark-results.json","w"), indent=1, default=str)
print("ALL DONE", flush=True)
print(json.dumps(R, indent=1, default=str)[:3000])

// Dokimos Forma icon audit: static HTML, pinned local registry, no dependencies.
import fs from "node:fs";
import path from "node:path";
import {fileURLToPath} from "node:url";
const THIS=fileURLToPath(import.meta.url);
const namePattern=/^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const attr=(html,name)=>{
  const rx=new RegExp("(?:^|\\s)"+name+"\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))","i");
  const m=rx.exec(html);
  return m ? m[1]??m[2]??m[3] : null;
};
const plain=s=>s.replace(/<!--[\s\S]*?-->/g,"").replace(/<[^>]+>/g,"").replace(/&nbsp;/gi," ").trim();
const line=(s,i)=>1+(s.slice(0,i).match(/\n/g)||[]).length;
export function loadIconPolicy(registry, config={}) {
  if(registry?.schemaVersion!==1||!Array.isArray(registry.icons)||!registry.icons.length)throw Error("A version-pinned Forma v1 registry is required");
  const names=new Set();
  for(const icon of registry.icons){
    if(!icon||!namePattern.test(icon.name)||names.has(icon.name))throw Error("Invalid or duplicate icon name");
    names.add(icon.name);
  }
  const deprecated=config.deprecated??{};
  if(!deprecated||typeof deprecated!=="object"||Array.isArray(deprecated))throw Error("Invalid deprecations map");
  for(const [old,newName] of Object.entries(deprecated)){
    if(!namePattern.test(old)||!names.has(newName))throw Error("Invalid deprecated alias");
  }
  return {names,deprecated,version:registry.formaVersion??null};
}
export function auditHtml(html,filename,policy) {
  const issues=[];
  const add=(i,rule,message,severity="error")=>issues.push({file:filename,line:line(html,i),rule,severity,message});
  const iconId=(name,i)=>{
    if(/[{}$]/.test(name))add(i,"icon-dynamic","Dynamic icon name requires an integration test","warning");
    else if(Object.hasOwn(policy.deprecated,name))add(i,"icon-deprecated",name+" is deprecated; use "+policy.deprecated[name]);
    else if(!policy.names.has(name))add(i,"icon-unknown","Unknown Forma icon: "+name);
  };
  for(const m of html.matchAll(/<[\w:-]+\b[^>]*\bdata-ef-icon\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))[^>]*>/gi))iconId(m[1]??m[2]??m[3],m.index);
  for(const m of html.matchAll(/<img\b[^>]*>/gi)){
    const src=attr(m[0],"src");
    const match=src?.match(/(?:^|\/)icons\/([a-z0-9-]+)\.svg(?:[?#].*)?$/i);
    if(match)iconId(match[1],m.index);
  }
  for(const m of html.matchAll(/<(button|a)\b([^>]*)>([\s\S]*?)<\/\1\s*>/gi)){
    const [,tag,attrs,inside]=m;
    if(!/(?:<ef-icon\b|data-ef-icon\s*=|\/icons\/[\w-]+\.svg)/i.test(inside))continue;
    const labeled=Boolean(plain(inside))||Boolean(attr(attrs,"aria-label")?.trim());
    const by=attr(attrs,"aria-labelledby");
    // Cross-referenced accessible name is confirmed by static matching ID+text.
    const labelRefs=by?.trim().split(/\s+/)||[];
    const refsValid=labelRefs.length>0&&labelRefs.every(id=>{
      if(!/^[\w:-]+$/.test(id))return false;
      const rx=new RegExp("<[a-z][\\w:-]*\\b[^>]*\\bid=[\"']"+id+"[\"'][^>]*>([\\s\\S]*?)<\\/[a-z][\\w:-]*>","i");
      const target=rx.exec(html);
      return target&&Boolean(plain(target[1]));
    });
    if(!labeled&&!refsValid)add(m.index,"icon-control-name",tag+" contains an icon without a verifiable accessible name");
    if(tag==="a"&&!attr(attrs,"href"))add(m.index,"icon-link-target","Icon link lacks an href");
  }
  for(const m of html.matchAll(/--ef-icon-size\s*:\s*([^;"'}]+)/gi)){
    const size=m[1].trim(), px=/^(\d+(?:\.\d+)?)px$/.exec(size);
    if(px&&![16,20,24,32,48].includes(Number(px[1])))add(m.index,"icon-size",size+" is not an approved optical size");
    else if(!px&&!/^(?:\d+(?:\.\d+)?(?:rem|em)|var\(--[\w-]+\))$/.test(size))
      add(m.index,"icon-size-review","Nonstandard size requires visual review: "+size,"warning");
  }
  return issues.sort((a,b)=>a.line-b.line||a.rule.localeCompare(b.rule));
}
export function auditDirectory(root,policy){
  const issues=[],ignored=new Set([".git",".ros","node_modules",".conditor","coverage","test-results"]);
  const walk=dir=>{
    for(const entry of fs.readdirSync(dir,{withFileTypes:true}).sort((a,b)=>a.name.localeCompare(b.name))){
      if(entry.isSymbolicLink())continue;
      const file=path.join(dir,entry.name);
      if(entry.isDirectory()&&!ignored.has(entry.name))walk(file);
      else if(entry.isFile()&&/\.html?$/i.test(entry.name))
        issues.push(...auditHtml(fs.readFileSync(file,"utf8"),path.relative(root,file),policy));
    }
  };
  walk(path.resolve(root));
  return issues;
}
function main(){
  const args=process.argv.slice(2),opt=flag=>{
    const at=args.indexOf(flag);
    if(at===-1)return null;
    if(!args[at+1]||args[at+1].startsWith("--"))throw Error(flag+" requires an argument");
    return args[at+1];
  };
  const root=opt("--root"),registry=opt("--registry"),deprecated=opt("--deprecated");
  if(!root||!registry)throw Error("Usage: node tools/icon-usage-audit.mjs --root HTML_DIR --registry PINNED/icons/registry.json [--deprecated FILE] [--json]");
  const policy=loadIconPolicy(JSON.parse(fs.readFileSync(registry,"utf8")),deprecated?JSON.parse(fs.readFileSync(deprecated,"utf8")):{});
  const findings=auditDirectory(root,policy);
  if(args.includes("--json"))console.log(JSON.stringify({formaVersion:policy.version,findings},null,2));
  else if(!findings.length)console.log("PASS: no static Forma icon usage issues");
  else for(const f of findings)console.log(f.file+":"+f.line+" ["+f.severity+"] "+f.rule+": "+f.message);
  if(findings.some(f=>f.severity==="error"))process.exitCode=1;
}
if(process.argv[1]&&path.resolve(process.argv[1])===THIS){
  try{main()}catch(e){console.error("Dokimos icon audit: "+e.message);process.exitCode=2;}
}

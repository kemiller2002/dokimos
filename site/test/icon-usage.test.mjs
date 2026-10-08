import assert from "node:assert/strict";
import test from "node:test";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import {auditHtml,auditDirectory,loadIconPolicy} from "../../tools/icon-usage-audit.mjs";

const registry={schemaVersion:1,formaVersion:"0.6.0",grid:24,icons:[
  {name:"email"},{name:"send"},{name:"reply"},{name:"warning"}
]};
const policy=loadIconPolicy(registry,{deprecated:{"old-mail":"email"}});
const failRules=html=>auditHtml(html,"fixture.html",policy).filter(x=>x.severity==="error").map(x=>x.rule);
test("pinned registry contract rejects duplicate IDs, invalid roots and bad deprecations",()=>{
  assert.equal(policy.version,"0.6.0");
  assert.throws(()=>loadIconPolicy({schemaVersion:2,icons:[{name:"email"}]}),/pinned/);
  assert.throws(()=>loadIconPolicy({...registry,icons:[{name:"email"},{name:"email"}]}),/duplicate/);
  assert.throws(()=>loadIconPolicy(registry,{deprecated:{"legacy":"missing"}}),/alias/);
});
test("recognizes compiled data-ef-icon and standalone Forma SVG references",()=>{
  assert.deepEqual(failRules('<span data-ef-icon="email"><svg></svg></span><img src="/assets/icons/send.svg" alt="">'),[]);
  assert.deepEqual(failRules('<span data-ef-icon="oops"></span>'),["icon-unknown"]);
  assert.deepEqual(failRules('<img src="../icons/not-registered.svg" alt="">'),["icon-unknown"]);
  assert.deepEqual(failRules('<span data-ef-icon="old-mail"></span>'),["icon-deprecated"]);
});
test("named native icon-only buttons are valid, unlabeled ones are rejected",()=>{
  const icon='<ef-icon><span data-ef-icon="email"><svg aria-hidden="true"></svg></span></ef-icon>';
  assert.deepEqual(failRules('<button type="button" aria-label="Send email">'+icon+"</button>"),[]);
  assert.deepEqual(failRules('<button type="button">'+icon+"</button>"),["icon-control-name"]);
  assert.deepEqual(failRules('<button type="button">'+icon+" <span class=\"ef-visually-hidden\">Send email</span></button>"),[]);
  assert.deepEqual(failRules('<p id="send-name">Send email</p><button type="button" aria-labelledby="send-name">'+icon+"</button>"),[]);
  assert.deepEqual(failRules('<button type="button" aria-labelledby="not-there">'+icon+"</button>"),["icon-control-name"]);
});
test("icon-bearing links must have both a name and an href",()=>{
  const html='<a><span data-ef-icon="email"></span></a>';
  assert.deepEqual(failRules(html),["icon-control-name","icon-link-target"]);
  assert.deepEqual(failRules('<a href="/email" aria-label="Open email"><span data-ef-icon="email"></span></a>'),[]);
});
test("consistency checks identify nonstandard hardcoded sizes without rejecting relative units",()=>{
  assert.deepEqual(failRules('<span style="--ef-icon-size:21px" data-ef-icon="email"></span>'),["icon-size"]);
  assert.deepEqual(failRules('<span style="--ef-icon-size:24px" data-ef-icon="email"></span>'),[]);
  assert.deepEqual(failRules('<span style="--ef-icon-size:1.25rem" data-ef-icon="email"></span>'),[]);
  const result=auditHtml('<span style="--ef-icon-size:calc(1rem + 4px)" data-ef-icon="email"></span>','x.html',policy);
  assert.equal(result[0].rule,"icon-size-review");
  assert.equal(result[0].severity,"warning");
});
test("dynamic IDs are reported as unverified, never misclassified as known",()=>{
  const result=auditHtml('<span data-ef-icon="{selectedIcon}"></span>','page.html',policy);
  assert.equal(result.length,1);
  assert.equal(result[0].rule,"icon-dynamic");
  assert.equal(result[0].severity,"warning");
});
test("directory scan produces deterministic file names, line numbers and respects symlinks",()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),"dokimos-icons-"));
  try{
    fs.mkdirSync(path.join(root,"a"));
    fs.writeFileSync(path.join(root,"a","page.html"),"<p>safe</p>\n<button><span data-ef-icon=\"unknown\"></span></button>\n");
    fs.mkdirSync(path.join(root,"node_modules"));
    fs.writeFileSync(path.join(root,"node_modules","other.html"),'<span data-ef-icon="unknown"></span>');
    const issues=auditDirectory(root,policy);
    assert.deepEqual(issues.map(x=>[x.file,x.line,x.rule]),[
      [path.join("a","page.html"),2,"icon-control-name"],
      [path.join("a","page.html"),2,"icon-unknown"]
    ]);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});

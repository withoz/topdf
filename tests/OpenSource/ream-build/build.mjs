import {readFile,writeFile,mkdir,copyFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import {build} from 'esbuild';

const here=path.dirname(fileURLToPath(import.meta.url));
const root=path.resolve(here,'../../..');
const vendor=path.join(root,'src/Topdf/engines/vendor/ream');
const staticFiles=path.join(root,'src/Topdf/engines/ream');
const packageRoot=path.join(here,'node_modules/reamkit');
const printModel=path.join(packageRoot,'dist/esm/excel/print-model.js');
const original=await readFile(printModel,'utf8');
const before='const room = columnWidths[c];';
const after=`// TOPDF fix: shrink merged-cell text against the entire visible merged width.
                const room = merge
                    ? columnWidths.slice(c, Math.min(colCount, merge.endColumn - colStart + 1))
                        .reduce((total, width, offset) => hiddenCols.has(c + offset) ? total : total + width, 0)
                    : columnWidths[c];`;
if(original.split(before).length!==2) throw new Error('Unexpected upstream shrinkToFit source; review before patching.');
const patched=original.replace(before,after);
await mkdir(path.join(vendor,'source'),{recursive:true});
await copyFile(path.join(staticFiles,'index.html'),path.join(vendor,'index.html'));
await copyFile(path.join(staticFiles,'NOTICE.txt'),path.join(vendor,'NOTICE.txt'));
await writeFile(path.join(vendor,'source/print-model.original.js'),original);
await writeFile(path.join(vendor,'source/print-model.patched.js'),patched);
await writeFile(path.join(vendor,'source/change.json'),JSON.stringify({upstream:'reamkit@1.33.1',originalSha256:createHash('sha256').update(original).digest('hex'),before,after},null,2));
await build({
 stdin:{contents:`import {Ream} from 'reamkit'; export async function convertXls(bytes, fonts) { const doc=Ream.parse(bytes); if(doc.format!=='xls') throw new Error('Expected binary XLS input'); return doc.convertWithReport('pdf',{fonts}); }`,resolveDir:here,sourcefile:'topdf-ream-entry.js'},
 outfile:path.join(vendor,'ream.bundle.mjs'),bundle:true,format:'esm',platform:'browser',target:'es2022',minify:false,
 plugins:[{name:'topdf-merged-shrink-fix',setup(builder){builder.onLoad({filter:/excel[\\/]print-model\.js$/},async args=>({contents:patched,loader:'js',resolveDir:path.dirname(args.path)}));}}],
});
for(const name of ['reamkit','fflate','fast-xml-parser','@nodable/entities','fast-xml-builder','anynum','path-expression-matcher','strnum','xml-naming']){
 const folder=path.join(here,'node_modules',name);
 try {
  const pkg=JSON.parse(await readFile(path.join(folder,'package.json'),'utf8'));
  const dest=path.join(vendor,'licenses',name.replace('/','_')); await mkdir(dest,{recursive:true});
  await writeFile(path.join(dest,'package.json'),JSON.stringify({name:pkg.name,version:pkg.version,license:pkg.license,repository:pkg.repository},null,2));
  for(const file of ['LICENSE','LICENSE.txt','LICENSE.md','license.txt','license.md']) {try{await copyFile(path.join(folder,file),path.join(dest,file));}catch{}}
 }catch{}
}
console.log('Built patched Ream browser bundle (no Node runtime dependency).');

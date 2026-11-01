// An explicit, hashed inventory avoids stale SMB directory enumeration on Windows.
import {readdir,readFile,writeFile} from 'node:fs/promises'
import {createHash} from 'node:crypto'
const root=new URL('../dist/',import.meta.url)
async function inventory(directory='') {
  const files=[]
  for(const entry of await readdir(new URL(directory,root),{withFileTypes:true})) {
    const path=directory+entry.name
    if(entry.isDirectory()) files.push(...await inventory(path+'/'))
    else if(path!=='asset-manifest.json') files.push({path,sha256:createHash('sha256').update(await readFile(new URL(path,root))).digest('hex')})
  }
  return files.sort((a,b)=>a.path.localeCompare(b.path))
}
await writeFile(new URL('asset-manifest.json',root),JSON.stringify(await inventory(),null,2)+'\n')

//! Private, resumable pointer scans. Worker nonces fence late filesystem work;
//! incomplete generations cannot replace the last complete public link index.
use super::{Db, Error, Pool, catalogue::Link, now_ms};
use std::collections::HashSet;
use std::fmt::Write as _;
use std::path::{Component, Path};
use uuid::Uuid;
const MAX_ENTRIES: i64 = 2_000_000;
const MAX_LINKS: i64 = 2_000_000;
const MAX_PATH_BYTES: i64 = 2_000_000_000;
const BATCH: usize = 256;
#[derive(Clone, Debug, sqlx::FromRow)]
pub struct Scan {
    pub root: String,
    pub scope: String,
    pub worker: String,
    pub device: String,
    pub inode: String,
    pub generation: i64,
    pub visited: i64,
    pub linked: i64,
    pub path_bytes: i64,
    pub started_at: i64,
    pub updated_at: i64,
}
#[derive(Clone, Debug, sqlx::FromRow)]
pub struct Directory {
    pub path: String,
    pub depth: i64,
}
#[derive(Clone, Debug)]
pub struct Batch {
    pub directory: String,
    pub seen: Vec<String>,
    pub directories: Vec<Directory>,
    pub links: Vec<Link>,
    pub complete: bool,
}
fn valid_path(root: &str, path: &str) -> bool {
    path.len() <= 4096
        && Path::new(path).is_absolute()
        && Path::new(path).starts_with(root)
        && !Path::new(path)
            .components()
            .any(|c| matches!(c, Component::ParentDir | Component::CurDir))
}
// Dynamic SQL contains only fixed table names and numbered placeholders. Values stay bound.
fn placeholders(start: usize, count: usize) -> String {
    let mut out = String::new();
    for n in start..start + count {
        if !out.is_empty() {
            out.push(',');
        }
        write!(out, "${n}").expect("String formatting is infallible");
    }
    out
}
impl Db {
    pub async fn catalogue_scans(&self) -> Result<Vec<Scan>, Error> {
        Ok(
            on!(self,pool=>sqlx::query_as("SELECT root,scope,worker,device,inode,generation,visited,linked,path_bytes,started_at,updated_at FROM library_scan ORDER BY root").fetch_all(pool).await?),
        )
    }
    /// Claims the next slice. Matching generations retain progress, while changed
    /// scope or physical identity starts a new private generation.
    pub async fn catalogue_scan_claim(
        &self,
        root: &str,
        scope: &str,
        worker: Uuid,
        identity: (&str, &str),
        generation: i64,
    ) -> Result<Scan, Error> {
        if !valid_path(root, root)
            || scope.is_empty()
            || scope.len() > 128
            || worker.is_nil()
            || generation < 0
        {
            return Err(Error::Corrupt("invalid library scan claim"));
        }
        let worker = worker.to_string();
        let now = now_ms();
        let row = on!(self,pool=>{
            let mut tx=pool.begin().await?;
            sqlx::query("INSERT INTO library_catalogue(root,scope) VALUES($1,$2) ON CONFLICT(root) DO NOTHING").bind(root).bind(scope).execute(&mut *tx).await?;
            let mut run:Scan=sqlx::query_as("INSERT INTO library_scan(root,scope,worker,device,inode,generation,started_at,updated_at) VALUES($1,$2,$3,$4,$5,$6,$7,$7) ON CONFLICT(root) DO UPDATE SET worker=excluded.worker,updated_at=excluded.updated_at RETURNING root,scope,worker,device,inode,generation,visited,linked,path_bytes,started_at,updated_at")
                .bind(root).bind(scope).bind(&worker).bind(identity.0).bind(identity.1).bind(generation).bind(now).fetch_one(&mut *tx).await?;
            if run.scope!=scope || run.device!=identity.0 || run.inode!=identity.1 {
                for table in ["library_scan_seen","library_scan_dirs","library_scan_links"] {
                    sqlx::query(sqlx::AssertSqlSafe(format!("DELETE FROM {table} WHERE root=$1"))).bind(root).execute(&mut *tx).await?;
                }
                sqlx::query("UPDATE library_scan SET scope=$2,device=$3,inode=$4,generation=$5,visited=0,linked=0,path_bytes=0,started_at=$6 WHERE root=$1")
                    .bind(root).bind(scope).bind(identity.0).bind(identity.1).bind(generation).bind(now).execute(&mut *tx).await?;
                run.scope=scope.into();run.device=identity.0.into();run.inode=identity.1.into();run.generation=generation;run.visited=0;run.linked=0;run.path_bytes=0;run.started_at=now;
            }
            sqlx::query("INSERT INTO library_scan_dirs(root,path,depth) VALUES($1,$1,0) ON CONFLICT(root,path) DO NOTHING").bind(root).execute(&mut *tx).await?;
            sqlx::query("UPDATE library_catalogue SET attempted_at=$2,attempted_generation=$3,error=NULL WHERE root=$1").bind(root).bind(now).bind(run.generation).execute(&mut *tx).await?;
            tx.commit().await?;run
        });
        Ok(row)
    }
    pub async fn catalogue_scan_directory(
        &self,
        root: &str,
        worker: &str,
    ) -> Result<Option<Directory>, Error> {
        Ok(
            on!(self,pool=>sqlx::query_as("SELECT d.path,d.depth FROM library_scan_dirs d JOIN library_scan r ON r.root=d.root WHERE d.root=$1 AND r.worker=$2 AND d.done=0 ORDER BY d.depth,d.path LIMIT 1").bind(root).bind(worker).fetch_optional(pool).await?),
        )
    }
    pub async fn catalogue_scan_seen(
        &self,
        root: &str,
        worker: &str,
        paths: &[String],
    ) -> Result<HashSet<String>, Error> {
        if paths.len() > BATCH {
            return Err(Error::Corrupt("library scan batch exceeds bound"));
        }
        if paths.is_empty() {
            return Ok(HashSet::new());
        }
        let sql = format!(
            "SELECT s.path FROM library_scan_seen s JOIN library_scan r ON r.root=s.root WHERE s.root=$1 AND r.worker=$2 AND s.path IN({})",
            placeholders(3, paths.len())
        );
        let seen: Vec<String> = on!(self,pool=>{
            let mut query=sqlx::query_scalar(sqlx::AssertSqlSafe(sql)).bind(root).bind(worker);for path in paths {query=query.bind(path);}query.fetch_all(pool).await?
        });
        Ok(seen.into_iter().collect())
    }
    /// The row update and every checkpoint mutation share a transaction. Once a
    /// new slice claims the run, an old physical worker cannot change its state.
    pub async fn catalogue_scan_checkpoint(
        &self,
        root: &str,
        worker: &str,
        batch: &Batch,
    ) -> Result<(), Error> {
        if batch.seen.len() > BATCH
            || batch.directories.len() > BATCH
            || batch.links.len() > BATCH
            || !valid_path(root, &batch.directory)
            || batch.seen.iter().any(|p| {
                !valid_path(root, p) || Path::new(p).parent() != Some(Path::new(&batch.directory))
            })
        {
            return Err(Error::Corrupt("invalid library scan batch"));
        }
        let unique: HashSet<_> = batch.seen.iter().collect();
        if unique.len() != batch.seen.len()
            || batch
                .directories
                .iter()
                .any(|d| !unique.contains(&d.path) || !(1..=64).contains(&d.depth))
            || batch.links.iter().any(|l| {
                l.root != root
                    || !unique.contains(&l.path)
                    || !matches!(l.link_kind, 1 | 2)
                    || !matches!(l.video, 0 | 1)
            })
        {
            return Err(Error::Corrupt(
                "library scan results do not match its entries",
            ));
        }
        on!(self,pool=>{
            let mut tx=pool.begin().await?;
            let locked=sqlx::query("UPDATE library_scan SET updated_at=$3 WHERE root=$1 AND worker=$2").bind(root).bind(worker).bind(now_ms()).execute(&mut *tx).await?.rows_affected();
            if locked!=1 {return Err(Error::Corrupt("library scan ownership changed"));}
            let depth:Option<i64>=sqlx::query_scalar("SELECT depth FROM library_scan_dirs WHERE root=$1 AND path=$2 AND done=0").bind(root).bind(&batch.directory).fetch_optional(&mut *tx).await?;
            let Some(depth)=depth else {return Err(Error::Corrupt("library scan directory is not pending"));};
            if batch.directories.iter().any(|d|d.depth!=depth+1) {return Err(Error::Corrupt("library scan directory depth changed"));}
            let mut fresh=HashSet::<String>::new();
            if !batch.seen.is_empty() {
                let values=(0..batch.seen.len()).map(|n|format!("($1,${})",n+2)).collect::<Vec<_>>().join(",");
                let sql=format!("INSERT INTO library_scan_seen(root,path) VALUES {values} ON CONFLICT(root,path) DO NOTHING RETURNING path");
                let mut query=sqlx::query_scalar(sqlx::AssertSqlSafe(sql)).bind(root);for path in &batch.seen {query=query.bind(path);}
                fresh=query.fetch_all(&mut *tx).await?.into_iter().collect();
            }
            let directories:Vec<_>=batch.directories.iter().filter(|d|fresh.contains(&d.path)).collect();
            if !directories.is_empty() {
                let values=(0..directories.len()).map(|n|format!("($1,${},${})",2+n*2,3+n*2)).collect::<Vec<_>>().join(",");
                let sql=format!("INSERT INTO library_scan_dirs(root,path,depth) VALUES {values} ON CONFLICT(root,path) DO NOTHING");
                let mut query=sqlx::query(sqlx::AssertSqlSafe(sql)).bind(root);
                for dir in directories {query=query.bind(&dir.path).bind(dir.depth);}
                query.execute(&mut *tx).await?;
            }
            let links:Vec<_>=batch.links.iter().filter(|l|fresh.contains(&l.path)).collect();
            if !links.is_empty() {
                let values=(0..links.len()).map(|n|format!("($1,{})",placeholders(2+n*7,7))).collect::<Vec<_>>().join(",");
                let sql=format!("INSERT INTO library_scan_links(root,path,relative_path,group_name,item_id,pointer_hash,link_kind,video) VALUES {values}");
                let mut query=sqlx::query(sqlx::AssertSqlSafe(sql)).bind(root);
                for link in &links {query=query.bind(&link.path).bind(&link.relative_path).bind(&link.group_name).bind(link.item_id).bind(&link.pointer_hash).bind(link.link_kind).bind(link.video);}
                query.execute(&mut *tx).await?;
            }
            let bytes:i64=fresh.iter().map(|p|i64::try_from(p.len()).unwrap_or(i64::MAX)).sum();
            let changed=sqlx::query("UPDATE library_scan SET visited=visited+$3,linked=linked+$4,path_bytes=path_bytes+$5 WHERE root=$1 AND worker=$2 AND visited+$3<=$6 AND linked+$4<=$7 AND path_bytes+$5<=$8")
                .bind(root).bind(worker).bind(i64::try_from(fresh.len()).map_err(|_|Error::Corrupt("scan count overflow"))?).bind(i64::try_from(links.len()).map_err(|_|Error::Corrupt("scan count overflow"))?).bind(bytes).bind(MAX_ENTRIES).bind(MAX_LINKS).bind(MAX_PATH_BYTES).execute(&mut *tx).await?.rows_affected();
            if changed!=1 {return Err(Error::Corrupt("library scan exceeds durable storage bound"));}
            if batch.complete {
                sqlx::query("UPDATE library_scan_dirs SET done=1 WHERE root=$1 AND path=$2").bind(root).bind(&batch.directory).execute(&mut *tx).await?;
            }
            tx.commit().await?;
        });
        Ok(())
    }
    /// Must be called while the current configuration read guard is held and the
    /// physical root identity still matches the claimed generation.
    pub async fn catalogue_scan_publish(
        &self,
        root: &str,
        scope: &str,
        worker: &str,
        at: i64,
    ) -> Result<bool, Error> {
        Ok(on!(self,pool=>{
            let mut tx=pool.begin().await?;
            let locked=sqlx::query("UPDATE library_scan SET updated_at=$4 WHERE root=$1 AND scope=$2 AND worker=$3").bind(root).bind(scope).bind(worker).bind(now_ms()).execute(&mut *tx).await?.rows_affected();
            if locked!=1 {return Ok(false);}
            let pending:i64=sqlx::query_scalar("SELECT COUNT(*) FROM library_scan_dirs WHERE root=$1 AND done=0").bind(root).fetch_one(&mut *tx).await?;
            if pending!=0 {return Ok(false);}
            let generation:i64=sqlx::query_scalar("SELECT generation FROM library_scan WHERE root=$1").bind(root).fetch_one(&mut *tx).await?;
            sqlx::query("DELETE FROM library_links WHERE root=$1").bind(root).execute(&mut *tx).await?;
            sqlx::query("INSERT INTO library_links(root,path,relative_path,group_name,item_id,pointer_hash,link_kind,video) SELECT root,path,relative_path,group_name,item_id,pointer_hash,link_kind,video FROM library_scan_links WHERE root=$1").bind(root).execute(&mut *tx).await?;
            sqlx::query("UPDATE library_catalogue SET scope=$2,scanned_at=$3,completed_generation=$4,error=NULL WHERE root=$1").bind(root).bind(scope).bind(at).bind(generation).execute(&mut *tx).await?;
            sqlx::query("DELETE FROM library_scan WHERE root=$1 AND worker=$2").bind(root).bind(worker).execute(&mut *tx).await?;
            tx.commit().await?;true
        }))
    }
    pub async fn catalogue_scan_fail(&self, root: &str, worker: &str) -> Result<(), Error> {
        on!(self,pool=>{
            let mut tx=pool.begin().await?;
            let changed=sqlx::query("DELETE FROM library_scan WHERE root=$1 AND worker=$2 RETURNING generation").bind(root).bind(worker).fetch_optional(&mut *tx).await?;
            if let Some(row)=changed {
                use sqlx::Row as _;
                let generation:i64=row.try_get("generation")?;
                sqlx::query("UPDATE library_catalogue SET error=$2,attempted_at=$3,attempted_generation=$4 WHERE root=$1").bind(root).bind("Library scan incomplete; last complete index retained").bind(now_ms()).bind(generation).execute(&mut *tx).await?;
            }
            tx.commit().await?;
        });
        Ok(())
    }
    /// Invalidates detached filesystem work without throwing away its accepted
    /// checkpoints. Storage timeouts back off; foreground yields resume promptly.
    pub async fn catalogue_scan_suspend(
        &self,
        root: &str,
        worker: &str,
        storage_wait: bool,
    ) -> Result<bool, Error> {
        let next = Uuid::new_v4().to_string();
        Ok(on!(self,pool=>{
            let mut tx=pool.begin().await?;
            let generation:Option<i64>=sqlx::query_scalar("UPDATE library_scan SET worker=$3,updated_at=$4 WHERE root=$1 AND worker=$2 RETURNING generation").bind(root).bind(worker).bind(&next).bind(now_ms()).fetch_optional(&mut *tx).await?;
            if let Some(generation)=generation {
                let error=storage_wait.then_some("Library scan waiting for storage; last complete index retained");
                sqlx::query("UPDATE library_catalogue SET error=$2,attempted_at=$3,attempted_generation=$4 WHERE root=$1").bind(root).bind(error).bind(now_ms()).bind(generation).execute(&mut *tx).await?;
                tx.commit().await?;true
            } else {false}
        }))
    }
    pub async fn catalogue_scan_abandon(&self, root: &str, worker: &str) -> Result<(), Error> {
        on!(self,pool=>sqlx::query("DELETE FROM library_scan WHERE root=$1 AND worker=$2").bind(root).bind(worker).execute(pool).await?.rows_affected());
        Ok(())
    }
    /// Remove derived snapshots/checkpoints for folders no longer configured.
    /// Source items and the filesystem are untouched; a re-added root is scanned again.
    pub async fn catalogue_retain(&self, roots: &[String]) -> Result<(), Error> {
        if roots.len() > 4096 {
            return Err(Error::Corrupt("too many library catalogue roots"));
        }
        if roots.is_empty() {
            on!(self,pool=>sqlx::query("DELETE FROM library_catalogue").execute(pool).await?.rows_affected());
        } else {
            let sql = format!(
                "DELETE FROM library_catalogue WHERE root NOT IN ({})",
                placeholders(1, roots.len())
            );
            on!(self,pool=>{let mut query=sqlx::query(sqlx::AssertSqlSafe(sql));for root in roots {query=query.bind(root);}query.execute(pool).await?.rows_affected()});
        }
        Ok(())
    }
}

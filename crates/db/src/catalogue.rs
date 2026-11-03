//! Complete library pointer snapshots. An incomplete scan may record uncertainty,
//! but cannot delete the last complete link index.
use super::{Db, Error, Pool};
use uuid::Uuid;

#[derive(Clone, Debug, PartialEq, Eq, sqlx::FromRow)]
pub struct Root {
    pub root: String,
    pub scope: String,
    pub scanned_at: Option<i64>,
    pub requested_at: i64,
    pub requested_generation: i64,
    pub completed_generation: i64,
    pub attempted_generation: i64,
    pub attempted_at: i64,
    pub error: Option<String>,
}
#[derive(Clone, Debug, PartialEq, Eq, sqlx::FromRow)]
pub struct Link {
    pub root: String,
    pub path: String,
    pub relative_path: String,
    pub group_name: String,
    pub item_id: Uuid,
    pub pointer_hash: String,
    /// 1 = symlink; 2 = signed STRM.
    pub link_kind: i16,
    pub video: i16,
}
#[derive(Clone, Debug, sqlx::FromRow)]
pub struct Group {
    pub root: String,
    pub group_name: String,
    pub files: i64,
    pub bytes: i64,
    pub unavailable: i64,
}

impl Db {
    pub async fn catalogue_roots(&self) -> Result<Vec<Root>, Error> {
        let sql = "SELECT root,scope,scanned_at,requested_at,requested_generation,completed_generation,attempted_generation,attempted_at,error FROM library_catalogue ORDER BY root";
        Ok(on!(self,pool=>sqlx::query_as(sql).fetch_all(pool).await?))
    }
    /// The caller must hold its configuration publication guard through this commit.
    pub async fn catalogue_replace(
        &self,
        root: &str,
        scope: &str,
        links: &[Link],
        at: i64,
        generation: i64,
    ) -> Result<(), Error> {
        if links.len() > 50_000 || links.iter().any(|l| l.root != root) {
            return Err(Error::Corrupt("invalid or oversized library snapshot"));
        }
        on!(self,pool=>{
            let mut tx=pool.begin().await?;
            sqlx::query("INSERT INTO library_catalogue(root,scope,scanned_at,attempted_at,completed_generation,attempted_generation) VALUES($1,$2,$3,$4,$5,$5) ON CONFLICT(root) DO UPDATE SET scope=excluded.scope,scanned_at=excluded.scanned_at,attempted_at=excluded.attempted_at,completed_generation=excluded.completed_generation,attempted_generation=excluded.attempted_generation,error=NULL")
                .bind(root).bind(scope).bind(at).bind(super::now_ms()).bind(generation).execute(&mut *tx).await?;
            sqlx::query("DELETE FROM library_links WHERE root=$1").bind(root).execute(&mut *tx).await?;
            for l in links {
                sqlx::query("INSERT INTO library_links(root,path,relative_path,group_name,item_id,pointer_hash,link_kind,video) VALUES($1,$2,$3,$4,$5,$6,$7,$8)")
                    .bind(root).bind(&l.path).bind(&l.relative_path).bind(&l.group_name).bind(l.item_id).bind(&l.pointer_hash).bind(l.link_kind).bind(l.video).execute(&mut *tx).await?;
            }
            tx.commit().await?;
        });
        Ok(())
    }
    pub async fn catalogue_request(&self, root: &str, scope: &str, at: i64) -> Result<(), Error> {
        let sql = "INSERT INTO library_catalogue(root,scope,requested_at,requested_generation) VALUES($1,$2,$3,1) ON CONFLICT(root) DO UPDATE SET requested_at=excluded.requested_at,requested_generation=library_catalogue.requested_generation+1";
        on!(self,pool=>sqlx::query(sql).bind(root).bind(scope).bind(at).execute(pool).await?.rows_affected());
        Ok(())
    }
    /// Failure does not adopt a new scope or clear a previous complete result.
    pub async fn catalogue_failed(
        &self,
        root: &str,
        scope: &str,
        error: &str,
        generation: i64,
    ) -> Result<(), Error> {
        let sql = "INSERT INTO library_catalogue(root,scope,error,attempted_at,attempted_generation) VALUES($1,$2,$3,$4,$5) ON CONFLICT(root) DO UPDATE SET error=excluded.error,attempted_at=excluded.attempted_at,attempted_generation=excluded.attempted_generation";
        on!(self,pool=>sqlx::query(sql).bind(root).bind(scope).bind(error).bind(super::now_ms()).bind(generation).execute(pool).await?.rows_affected());
        Ok(())
    }
    pub async fn catalogue_links(
        &self,
        root: Option<&str>,
        group: Option<&str>,
        item: Option<Uuid>,
        video: bool,
        after: &str,
        limit: u32,
    ) -> Result<Vec<Link>, Error> {
        let sql = "SELECT root,path,relative_path,group_name,item_id,pointer_hash,link_kind,video FROM library_links WHERE (root=$1 OR $1 IS NULL) AND (group_name=$2 OR $2 IS NULL) AND (item_id=$3 OR $3 IS NULL) AND path>$4 AND (NOT $6 OR video=1) ORDER BY path,root LIMIT $5";
        Ok(
            on!(self,pool=>sqlx::query_as(sql).bind(root).bind(group).bind(item).bind(after).bind(i64::from(limit.clamp(1,1000))).bind(video).fetch_all(pool).await?),
        )
    }
    pub async fn catalogue_groups(
        &self,
        root: &str,
        after: &str,
        limit: u32,
    ) -> Result<Vec<Group>, Error> {
        let sql = "SELECT l.root,l.group_name,CAST(COUNT(*) AS BIGINT) AS files,CAST(COALESCE(SUM(i.size),0) AS BIGINT) AS bytes,CAST(SUM(CASE WHEN i.id IS NULL OR i.health_state=5 THEN 1 ELSE 0 END) AS BIGINT) AS unavailable FROM library_links l LEFT JOIN items i ON i.id=l.item_id WHERE l.root=$1 AND l.group_name>$2 AND l.video=1 GROUP BY l.root,l.group_name ORDER BY l.group_name LIMIT $3";
        Ok(
            on!(self,pool=>sqlx::query_as(sql).bind(root).bind(after).bind(i64::from(limit.clamp(1,200))).fetch_all(pool).await?),
        )
    }
}

//! Bounded tree reader. DTDs/entities are rejected; no external resource is resolved.
use crate::Error;
use quick_xml::events::{BytesStart, Event};
use std::collections::BTreeMap;
#[derive(Clone, Debug, Default)]
pub(crate) struct Node {
    pub name: String,
    pub attrs: BTreeMap<String, String>,
    pub children: Vec<Self>,
}
impl Node {
    pub fn bytes(&self) -> usize {
        std::mem::size_of::<Self>()
            + self.name.len()
            + self
                .attrs
                .iter()
                .map(|(key, value)| key.len() + value.len() + 64)
                .sum::<usize>()
            + self.children.iter().map(Self::bytes).sum::<usize>()
    }

    pub fn text(&self, key: &str) -> Option<&str> {
        self.attrs.get(key).map(String::as_str)
    }
    pub fn number<T: std::str::FromStr>(&self, key: &str) -> Option<T> {
        self.text(key)?.parse().ok()
    }
    pub fn children<'a>(&'a self, name: &'a str) -> impl Iterator<Item = &'a Self> + 'a {
        self.children.iter().filter(move |n| n.name == name)
    }
}
fn node(tag: &BytesStart<'_>) -> Result<Node, Error> {
    let mut attrs = BTreeMap::new();
    for attr in tag.attributes() {
        let attr = attr.map_err(|_| Error::Invalid("invalid Plex XML attribute"))?;
        let key = attr.key.as_ref().to_owned();
        let value = attr
            .normalized_value(quick_xml::XmlVersion::Implicit1_0)
            .map_err(|_| Error::Invalid("invalid Plex XML attribute"))?
            .into_owned();
        if attrs.insert(key, value).is_some() {
            return Err(Error::Invalid("duplicate Plex XML attribute"));
        }
    }
    Ok(Node {
        name: tag.name().as_ref().to_owned(),
        attrs,
        children: vec![],
    })
}
pub(crate) fn read(bytes: &[u8]) -> Result<Node, Error> {
    if bytes.len() > 4 * 1024 * 1024 {
        return Err(Error::Invalid("Plex XML exceeds response limit"));
    }
    let mut reader = quick_xml::Reader::from_reader(bytes);
    let mut stack = vec![Node::default()];
    let mut count = 0_u32;
    loop {
        match reader
            .read_event()
            .map_err(|_| Error::Invalid("invalid Plex XML"))?
        {
            Event::Start(tag) => {
                count += 1;
                if count > 100_000 || stack.len() > 64 {
                    return Err(Error::Invalid("Plex XML exceeds structural limits"));
                }
                stack.push(node(&tag)?);
            }
            Event::Empty(tag) => {
                count += 1;
                if count > 100_000 || stack.len() > 64 {
                    return Err(Error::Invalid("Plex XML exceeds structural limits"));
                }
                stack
                    .last_mut()
                    .expect("sentinel")
                    .children
                    .push(node(&tag)?);
            }
            Event::End(_) => {
                if stack.len() < 2 {
                    return Err(Error::Invalid("unbalanced Plex XML"));
                }
                let node = stack.pop().expect("checked");
                stack.last_mut().expect("sentinel").children.push(node);
            }
            Event::DocType(_) | Event::GeneralRef(_) => {
                return Err(Error::Invalid("Plex XML declarations are not allowed"));
            }
            Event::Eof => break,
            _ => {}
        }
    }
    if stack.len() != 1 || stack[0].children.len() != 1 {
        return Err(Error::Invalid("Plex XML needs one root"));
    }
    Ok(stack
        .pop()
        .expect("sentinel")
        .children
        .pop()
        .expect("one root"))
}

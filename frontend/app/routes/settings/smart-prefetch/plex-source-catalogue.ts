import type { PlexLibrary, PlexSource } from "../plex/plex-api";
import type { DisabledPlexLibrary, PrefetchSource } from "./smart-prefetch-model";

export type PlexMediaSection = "movie" | "show";
export type PlexRecommendation = "recently-added" | "on-deck" | "continue-watching";
export type PlexLibraryIdentity = {
  serverId: string;
  libraryId: string;
  type: PlexMediaSection;
};
export type PlexCatalogueLibrary = {
  identity: PlexLibraryIdentity;
  title: string;
  hubs: PlexSource[];
  collections: PlexSource[];
  unavailable: PrefetchSource[];
  configured: boolean;
  enabled: boolean;
};

type CatalogueInput = {
  serverId: string;
  libraries: PlexLibrary[];
  sources: PlexSource[];
  savedSources: PrefetchSource[];
  disabledLibraries: DisabledPlexLibrary[];
};

/** Group title for server-wide hubs that belong to no single library. */
export const HOME_SCREEN_HUBS = "Home screen hubs";

/** A hub backed by a library collection that Plex shows on the home screen. */
export function isHomeCollection(source: PlexSource): boolean {
  return source.kind === "hub" && source.key.startsWith("/library/collections/");
}

export type PlexSourceMatch = { library: PlexCatalogueLibrary; sources: PlexSource[] };

/** Hubs and collections whose title contains the query, grouped by library in catalogue order. */
export function searchPlexCatalogue(
  catalogue: { movie: PlexCatalogueLibrary[]; show: PlexCatalogueLibrary[] },
  query: string,
): PlexSourceMatch[] {
  const needle = query.trim().toLowerCase();
  if (!needle) return [];
  return [...catalogue.movie, ...catalogue.show]
    .map((library) => ({
      library,
      sources: [...library.hubs, ...library.collections].filter((source) =>
        source.title.toLowerCase().includes(needle),
      ),
    }))
    .filter((match) => match.sources.length > 0);
}

export type EnabledPlexSource = { source: PrefetchSource; libraryTitle: string };

/** Enabled saved sources for one server, labelled with the library they warm from. */
export function enabledPlexSources(
  serverId: string,
  savedSources: PrefetchSource[],
  catalogue: { movie: PlexCatalogueLibrary[]; show: PlexCatalogueLibrary[] },
): EnabledPlexSource[] {
  const libraries = [...catalogue.movie, ...catalogue.show];
  return savedSources
    .filter((source) => source.Enabled && source.ServerId === serverId)
    .map((source) => ({
      source,
      libraryTitle:
        libraries.find(
          (library) =>
            library.identity.libraryId === source.LibraryId &&
            library.identity.type === mediaSection(source.Type),
        )?.title ?? (source.LibraryId ? "Saved library" : HOME_SCREEN_HUBS),
    }));
}

export function mediaSection(type: string): PlexMediaSection | null {
  if (type === "movie") return "movie";
  if (type === "show" || type === "episode") return "show";
  return null;
}

export function catalogueSourceKey(source: PlexSource): string {
  return `${source.serverId}\n${source.libraryId ?? ""}\n${source.kind}\n${source.key}`;
}

export function persistedSourceKey(serverId: string, kind: string, key: string): string {
  return `${serverId}\n${kind}\n${key}`;
}

function normalizedSearchValue(value: string): string {
  return value.toLowerCase().replaceAll(/[^a-z0-9]/g, "");
}

function classifyRecommendation(value: string): PlexRecommendation | null {
  if (value.includes("recentlyadded")) return "recently-added";
  if (value.includes("ondeck")) return "on-deck";
  if (value.includes("continuewatching")) return "continue-watching";
  return null;
}

export function recommendationFor(source: PlexSource): PlexRecommendation | null {
  return (
    classifyRecommendation(normalizedSearchValue(source.id)) ??
    classifyRecommendation(normalizedSearchValue(source.key)) ??
    classifyRecommendation(normalizedSearchValue(source.title))
  );
}

function identityKey(identity: PlexLibraryIdentity): string {
  return `${identity.serverId}\n${identity.libraryId}\n${identity.type}`;
}

function savedMediaSection(source: PrefetchSource): PlexMediaSection | null {
  return mediaSection(source.Type);
}

function compareSources(left: PlexSource, right: PlexSource): number {
  const recommendationDifference =
    Number(recommendationFor(right) !== null) - Number(recommendationFor(left) !== null);
  return recommendationDifference || left.title.localeCompare(right.title);
}

export function buildPlexSourceCatalogue({
  serverId,
  libraries,
  sources,
  savedSources,
  disabledLibraries,
}: CatalogueInput): { movie: PlexCatalogueLibrary[]; show: PlexCatalogueLibrary[] } {
  const sections: { movie: PlexCatalogueLibrary[]; show: PlexCatalogueLibrary[] } = {
    movie: [],
    show: [],
  };
  const groups = new Map<string, PlexCatalogueLibrary>();
  const librariesById = new Map(libraries.map((library) => [library.id, library]));

  const addGroup = (identity: PlexLibraryIdentity, title: string) => {
    const key = identityKey(identity);
    const existing = groups.get(key);
    if (existing) return existing;
    const configured = savedSources.some(
      (source) =>
        source.ServerId === identity.serverId &&
        source.LibraryId === identity.libraryId &&
        savedMediaSection(source) === identity.type,
    );
    const disabled = disabledLibraries.some(
      (library) =>
        library.ServerId === identity.serverId &&
        library.LibraryId === identity.libraryId &&
        library.Type === identity.type,
    );
    const group: PlexCatalogueLibrary = {
      identity,
      title,
      hubs: [],
      collections: [],
      unavailable: [],
      configured,
      enabled: configured && !disabled,
    };
    groups.set(key, group);
    sections[identity.type].push(group);
    return group;
  };

  for (const library of libraries) {
    const type = mediaSection(library.type);
    if (type) addGroup({ serverId, libraryId: library.id, type }, library.title);
  }

  // Plex repeats a library's hubs among the server-wide home hubs; each source is listed once.
  const libraryKeys = new Set(
    sources
      .filter(
        (source) =>
          source.serverId === serverId && source.libraryId && librariesById.has(source.libraryId),
      )
      .map((source) => source.key),
  );
  const catalogueKeys = new Set<string>();
  for (const source of sources) {
    if (source.serverId !== serverId || catalogueKeys.has(catalogueSourceKey(source))) continue;
    if (!source.libraryId && libraryKeys.has(source.key)) continue;
    const library = source.libraryId ? librariesById.get(source.libraryId) : undefined;
    const type = library ? mediaSection(library.type) : mediaSection(source.type);
    if (!type) continue;
    catalogueKeys.add(catalogueSourceKey(source));
    const group = addGroup(
      { serverId, libraryId: source.libraryId ?? "", type },
      library?.title ?? HOME_SCREEN_HUBS,
    );
    if (source.kind === "collection") group.collections.push(source);
    else if (source.kind === "hub") group.hubs.push(source);
  }

  const available = new Set(
    sources.map((source) => persistedSourceKey(source.serverId, source.kind, source.key)),
  );
  for (const source of savedSources) {
    if (
      source.ServerId !== serverId ||
      available.has(persistedSourceKey(source.ServerId, source.Kind, source.Key))
    )
      continue;
    const type = savedMediaSection(source);
    if (!type) continue;
    const library = source.LibraryId ? librariesById.get(source.LibraryId) : undefined;
    const group = addGroup(
      { serverId, libraryId: source.LibraryId, type },
      library?.title ?? (source.LibraryId ? "Saved library" : HOME_SCREEN_HUBS),
    );
    group.unavailable.push(source);
  }

  for (const group of groups.values()) {
    // A collection Plex promotes to the home screen is also a hub with the same key: list it once.
    const hubKeys = new Set(group.hubs.map((source) => source.key));
    group.collections = group.collections.filter((source) => !hubKeys.has(source.key));
    group.hubs.sort(compareSources);
    group.collections.sort((left, right) => left.title.localeCompare(right.title));
    group.unavailable.sort((left, right) => left.Title.localeCompare(right.Title));
  }
  return sections;
}

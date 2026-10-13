import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ArrowUp, Bell, Search } from "lucide-react";
import { Link } from "react-router-dom";
import { api, TMDB_IMAGE } from "../api";
import type { MediaSummary, MediaType } from "../types";

type Filter = "All" | MediaType;
type Sort = "newest" | "oldest" | "title";
const LIBRARY_SCROLL_KEY = "mediahub.library-scroll";

function formatProgress(progress: number): string {
  return `${progress.toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} %`;
}

function getSavedScrollPosition(): number | null {
  const savedPosition = Number(sessionStorage.getItem(LIBRARY_SCROLL_KEY));
  return Number.isFinite(savedPosition) && savedPosition > 0
    ? savedPosition
    : null;
}

const FILTERS: { key: Filter; label: string }[] = [
  { key: "All", label: "Tous" },
  { key: "Movie", label: "Films" },
  { key: "Series", label: "Séries" },
  { key: "Anime", label: "Animés" },
];

const MEDIA_TYPE_LABELS: Record<MediaType, string> = {
  Movie: "Film",
  Series: "Série",
  Anime: "Animé",
};

const PIPELINE_STAGE_LABELS: Record<
  NonNullable<MediaSummary["pipelineStage"]>,
  string
> = {
  Search: "Recherche",
  Download: "Téléchargement",
  TrackSelection: "Choix des pistes",
  Encode: "Encodage",
  Upload: "Upload",
  Notify: "Finalisation",
  Scan: "Scan",
  MetadataRefresh: "Métadonnées",
};

function stateLabel(media: MediaSummary): string {
  if (media.pipelineStage)
    return `Pipeline · ${PIPELINE_STAGE_LABELS[media.pipelineStage]}`;
  if (media.inStock) return "Dans le stock";
  return "Wishlist";
}

function latestEpisodeLabel(media: MediaSummary): string | null {
  if (media.type === "Movie" || !media.latestAiredEpisodeDate) return null;
  const date = new Date(
    `${media.latestAiredEpisodeDate}T00:00:00`,
  ).toLocaleDateString("fr-FR", {
    day: "2-digit",
    month: "short",
    year: "numeric",
  });
  if (media.missingAiredEpisodeCount === 0)
    return `Dernier épisode · ${date} · À jour`;
  return `Dernier épisode · ${date} · ${media.missingAiredEpisodeCount} manquant${media.missingAiredEpisodeCount > 1 ? "s" : ""}`;
}

function releaseDateLabel(media: MediaSummary): string | null {
  if (media.type !== "Movie" || !media.releaseDate) return null;
  const date = new Date(`${media.releaseDate}T00:00:00`).toLocaleDateString(
    "fr-FR",
    {
      day: "2-digit",
      month: "short",
      year: "numeric",
    },
  );
  return `Sortie · ${date}`;
}

export default function Library() {
  const [items, setItems] = useState<MediaSummary[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [scanning, setScanning] = useState(false);
  const [scanProgress, setScanProgress] = useState(0);
  const [filter, setFilter] = useState<Filter>("All");
  const [sort, setSort] = useState<Sort>("newest");
  const [outOfStockFirst, setOutOfStockFirst] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const [showScrollTop, setShowScrollTop] = useState(false);
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const restoredScrollRef = useRef(false);
  const savedScrollPositionRef = useRef<number | null>(
    getSavedScrollPosition(),
  );

  const normalizedSearchTerm = searchTerm.trim().toLocaleLowerCase("fr");
  const visibleItems = items
    .filter((item) => filter === "All" || item.type === filter)
    .filter((item) =>
      [item.title, item.storageName].some((name) =>
        name?.toLocaleLowerCase("fr").includes(normalizedSearchTerm),
      ),
    )
    .sort((left, right) => {
      if (sort === "title") return left.title.localeCompare(right.title, "fr");

      if (outOfStockFirst) {
        const stockDifference = Number(left.inStock) - Number(right.inStock);
        if (stockDifference !== 0) return stockDifference;
      }

      const leftDate =
        left.latestAiredEpisodeDate ??
        left.releaseDate ??
        `${left.year ?? 0}-01-01`;
      const rightDate =
        right.latestAiredEpisodeDate ??
        right.releaseDate ??
        `${right.year ?? 0}-01-01`;
      return sort === "oldest"
        ? leftDate.localeCompare(rightDate)
        : rightDate.localeCompare(leftDate);
    });
  const outOfStockItems = visibleItems.filter((item) => !item.inStock);
  const inStockItems = visibleItems.filter((item) => item.inStock);

  async function load() {
    try {
      setItems(await api.getLibrary());
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    load();
    const saveScrollPosition = () => {
      sessionStorage.setItem(LIBRARY_SCROLL_KEY, String(window.scrollY));
      setShowScrollTop(window.scrollY > 320);
    };
    window.addEventListener("scroll", saveScrollPosition, { passive: true });
    setShowScrollTop(window.scrollY > 320);

    return () => {
      if (pollRef.current) clearInterval(pollRef.current);
      window.removeEventListener("scroll", saveScrollPosition);
    };
  }, []);

  useLayoutEffect(() => {
    if (loading || restoredScrollRef.current) return;
    const savedPosition = savedScrollPositionRef.current;
    if (savedPosition === null) {
      restoredScrollRef.current = true;
      return;
    }

    let restoreFrame = requestAnimationFrame(() => {
      restoreFrame = requestAnimationFrame(() => {
        window.scrollTo(0, savedPosition);
        restoredScrollRef.current = true;
      });
    });
    return () => cancelAnimationFrame(restoreFrame);
  }, [loading]);

  function saveScrollPosition() {
    sessionStorage.setItem(LIBRARY_SCROLL_KEY, String(window.scrollY));
  }

  function renderGrid(mediaItems: MediaSummary[]) {
    return (
      <div className="grid">
        {mediaItems.map((m) => (
          <Link
            key={m.id}
            to={`/media/${m.id}`}
            className="card"
            onClick={saveScrollPosition}
          >
            {m.posterPath ? (
              <img
                src={`${TMDB_IMAGE}${m.posterPath}`}
                alt={m.title}
                loading="lazy"
              />
            ) : (
              <div className="poster-placeholder" />
            )}
            <div className="card-body">
              <div className="library-card-title">
                <strong>{m.title}</strong>
                {m.watchActive && <span className="library-watch-icon" role="img" aria-label="Veille active" title="Veille active">
                  <Bell size={14} aria-hidden="true" />
                </span>}
              </div>
              <div className="card-meta">
                <span
                  className={`badge media-type media-type-${m.type.toLowerCase()}`}
                >
                  {MEDIA_TYPE_LABELS[m.type]}
                </span>
                {m.year && <span className="muted">{m.year}</span>}
              </div>
              <span
                className={`badge status-${m.pipelineStage ? "processing" : m.inStock ? "available" : m.status.toLowerCase()}`}
              >
                {stateLabel(m)}
              </span>
              {latestEpisodeLabel(m) && (
                <span
                  className={`episode-freshness${m.missingAiredEpisodeCount > 0 ? " missing" : ""}`}
                >
                  {latestEpisodeLabel(m)}
                </span>
              )}
              {releaseDateLabel(m) && (
                <span className="episode-freshness">{releaseDateLabel(m)}</span>
              )}
            </div>
          </Link>
        ))}
      </div>
    );
  }

  async function startScan() {
    try {
      await api.scanStorage();
      setScanning(true);
      setScanProgress(0);
      if (pollRef.current) clearInterval(pollRef.current);
      pollRef.current = setInterval(async () => {
        await load();
        const jobs = await api.getJobs();
        const scan = jobs.find((j) => j.kind === "Scan");
        if (scan) {
          setScanProgress(scan.progress);
          if (scan.status !== "Running" && scan.status !== "Queued") {
            if (pollRef.current) clearInterval(pollRef.current);
            setScanning(false);
            await load();
          }
        }
      }, 2500);
    } catch (e) {
      setError((e as Error).message);
      setScanning(false);
    }
  }

  return (
    <div>
      <div className="toolbar">
        <div className="filters">
          {FILTERS.map((f) => {
            const count =
              f.key === "All"
                ? items.length
                : items.filter((m) => m.type === f.key).length;
            return (
              <button
                key={f.key}
                className={`filter-btn${filter === f.key ? " active" : ""}`}
                onClick={() => setFilter(f.key)}
              >
                {f.label} <span className="filter-count">{count}</span>
              </button>
            );
          })}
        </div>
        <label className="library-search">
          <Search size={18} aria-hidden="true" />
          <input
            type="search"
            value={searchTerm}
            onChange={(event) => setSearchTerm(event.target.value)}
            placeholder="Rechercher dans la bibliothèque"
            aria-label="Rechercher dans la bibliothèque"
          />
        </label>
        <label className="sort-control">
          Trier par
          <select
            value={sort}
            onChange={(event) => setSort(event.target.value as Sort)}
          >
            <option value="newest">
              Date de sortie / épisode : plus récent
            </option>
            <option value="oldest">
              Date de sortie / épisode : plus ancien
            </option>
            <option value="title">Titre : A à Z</option>
          </select>
        </label>
        <label className="stock-priority">
          <input
            type="checkbox"
            checked={outOfStockFirst}
            onChange={(event) => setOutOfStockFirst(event.target.checked)}
          />
          Wishlist en premier
        </label>
        <button onClick={startScan} disabled={scanning}>
          {scanning
            ? `Scan en cours… ${formatProgress(scanProgress)}`
            : "Scanner mon stock"}
        </button>
      </div>

      {scanning && (
        <div className="progress wide">
          <div
            className="progress-bar active"
            style={{ width: `${scanProgress}%` }}
          />
          <span className="progress-label">{formatProgress(scanProgress)}</span>
        </div>
      )}

      {error && <p className="error">Erreur : {error}</p>}

      {loading ? (
        <p>Chargement…</p>
      ) : items.length === 0 ? (
        <p>
          Bibliothèque vide. Ajoute un titre depuis la recherche, ou clique «
          Scanner mon stock ».
        </p>
      ) : visibleItems.length === 0 ? (
        <p className="muted">Aucun titre ne correspond à votre recherche.</p>
      ) : outOfStockFirst ? (
        <div className="library-stock-groups">
          {outOfStockItems.length > 0 && (
            <section className="library-stock-group">
              <h2>
                Wishlist <span>{outOfStockItems.length}</span>
              </h2>
              {renderGrid(outOfStockItems)}
            </section>
          )}
          {inStockItems.length > 0 && (
            <section className="library-stock-group">
              <h2>
                Dans mon stock <span>{inStockItems.length}</span>
              </h2>
              {renderGrid(inStockItems)}
            </section>
          )}
        </div>
      ) : (
        renderGrid(visibleItems)
      )}

      {showScrollTop && (
        <button
          className="scroll-top"
          onClick={() => window.scrollTo({ top: 0, behavior: "smooth" })}
          aria-label="Revenir en haut de la bibliothèque"
          title="Revenir en haut"
        >
          <ArrowUp size={20} aria-hidden="true" />
        </button>
      )}
    </div>
  );
}

import { useEffect, useRef, useState } from "react";
import { ArrowDown, ArrowUp, ArrowUpDown, RefreshCw, Trash2 } from "lucide-react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api, TMDB_IMAGE } from "../api";
import type { EpisodeNumberingRule, JobDto, MediaDetail as MediaDetailType, MediaStatus, ReleaseDto, StockMediaFile, TmdbSearchItem } from "../types";

function formatSize(bytes: number): string {
  if (!bytes) return "—";
  const gb = bytes / 1024 ** 3;
  if (gb >= 1) return `${gb.toFixed(2)} Go`;
  return `${(bytes / 1024 ** 2).toFixed(0)} Mo`;
}

function formatEncodingSpeed(framesPerSecond: number): string {
  return `${framesPerSecond.toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} fps`;
}

function formatProgress(progress: number): string {
  return `${progress.toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} %`;
}

function formatRemainingTime(seconds?: number | null): string | null {
  if (seconds === null || seconds === undefined) return null;
  const totalSeconds = Math.max(0, Math.ceil(seconds));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const remainingSeconds = totalSeconds % 60;
  if (hours > 0) return `~ ${hours} h ${String(minutes).padStart(2, "0")} min`;
  if (minutes > 0) return `~ ${minutes} min ${String(remainingSeconds).padStart(2, "0")} s`;
  return `~ ${remainingSeconds} s`;
}

function formatDate(iso?: string | null): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleDateString("fr-FR", {
    day: "2-digit",
    month: "short",
    year: "numeric",
  });
}

function formatDuration(duration?: string | null): string {
  if (!duration) return "—";
  const [hours, minutes, seconds] = duration.split(":");
  return `${Number(hours)} h ${minutes} min ${seconds.slice(0, 2)} s`;
}

function formatResolution(width?: number | null, height?: number | null): string {
  if (!width || !height) return "—";
  const label = width >= 3800 ? "2160p" : width >= 1900 ? "1080p" : width >= 1260 ? "720p" : height >= 550 ? "576p" : height >= 450 ? "480p" : null;
  return label ? `${label} (${width}×${height})` : `${width}×${height}`;
}

function formatBitRate(bitRate?: number | null): string {
  if (!bitRate) return "—";
  return bitRate >= 1_000_000
    ? `${(bitRate / 1_000_000).toLocaleString("fr-FR", { maximumFractionDigits: 2 })} Mb/s`
    : `${Math.round(bitRate / 1_000)} kb/s`;
}

const PIPELINE_STAGE_LABELS: Record<NonNullable<MediaDetailType["pipelineStage"]>, string> = {
  Search: "Recherche de source",
  Download: "Téléchargement",
  TrackSelection: "Choix des pistes",
  Encode: "Conversion HandBrake",
  Upload: "Upload vers le stock",
  Notify: "Finalisation",
  Scan: "Scan du stock",
  MetadataRefresh: "Actualisation des métadonnées",
};

function stateLabel(media: MediaDetailType): string {
  if (media.pipelineStage) return `Dans la pipeline · ${PIPELINE_STAGE_LABELS[media.pipelineStage]}`;
  if (media.status === "Ignored") return "Ignoré";
  return "Hors stock";
}

const JOB_STAGE_LABELS: Record<JobDto["kind"], string> = {
  Search: "Recherche de source",
  Download: "Téléchargement",
  TrackSelection: "Choix des pistes",
  Encode: "Conversion HandBrake",
  Upload: "Upload vers le stock",
  Notify: "Finalisation",
  Scan: "Scan du stock",
  MetadataRefresh: "Actualisation des métadonnées",
};

const JOB_STATUS_LABELS: Record<JobDto["status"], string> = {
  Queued: "En attente",
  Running: "En cours",
  Completed: "Terminé",
  Failed: "Échec",
  Cancelled: "Annulé",
  WaitingForSelection: "Choix requis",
};

const LANGUAGE_LABELS: Record<string, string> = {
  deu: "Allemand",
  eng: "Anglais",
  fra: "Français",
  fre: "Français",
  ita: "Italien",
  jpn: "Japonais",
  kor: "Coréen",
  spa: "Espagnol",
};

type ReleaseSortKey =
  | "title"
  | "coverage"
  | "publishDate"
  | "resolution"
  | "videoCodec"
  | "audioCodec"
  | "source"
  | "isMulti"
  | "sizeBytes"
  | "seeders"
  | "score"
  | "accepted";

type SortDirection = "asc" | "desc";

const RELEASE_SORT_COLUMNS: { key: ReleaseSortKey; label: string }[] = [
  { key: "title", label: "Nom" },
  { key: "coverage", label: "Couverture" },
  { key: "publishDate", label: "Date" },
  { key: "resolution", label: "Rés." },
  { key: "videoCodec", label: "Codec" },
  { key: "audioCodec", label: "Audio" },
  { key: "source", label: "Source" },
  { key: "isMulti", label: "MULTi" },
  { key: "sizeBytes", label: "Taille" },
  { key: "seeders", label: "Seeds" },
  { key: "score", label: "Score" },
  { key: "accepted", label: "Verdict" },
];

function formatLanguage(language?: string | null): string {
  return language ? LANGUAGE_LABELS[language.toLowerCase()] ?? language.toUpperCase() : "Inconnue";
}

function AudioTrackList({ tracks }: { tracks: StockMediaFile["audioTracks"] }) {
  const visibleTracks = tracks.filter((track) => track.language || track.codec || track.channels || track.sampleRate);
  if (visibleTracks.length === 0) return "—";

  return (
    <div className="media-track-list">
      {visibleTracks.map((track, index) => (
        <div key={index} className="media-track">
          <span className="track-language">{formatLanguage(track.language)}</span>
          <span>{[track.codec, track.channels, track.sampleRate ? `${track.sampleRate / 1000} kHz` : null].filter(Boolean).join(" · ")}</span>
        </div>
      ))}
    </div>
  );
}

function SubtitleTrackList({ tracks }: { tracks: StockMediaFile["subtitleTracks"] }) {
  if (tracks.length === 0) return "—";

  return (
    <div className="media-track-list">
      {tracks.map((track, index) => (
        <div key={index} className="media-track">
          <span className="track-language">{formatLanguage(track.language)}</span>
          <span>{[track.format, track.source].filter(Boolean).join(" · ")}</span>
          {track.isForced && <span className="track-flag">Forcé</span>}
          {track.isDefault && <span className="track-flag">Défaut</span>}
        </div>
      ))}
    </div>
  );
}

export default function MediaDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [media, setMedia] = useState<MediaDetailType | null>(null);
  const [mediaJobs, setMediaJobs] = useState<JobDto[]>([]);
  const [releases, setReleases] = useState<ReleaseDto[]>([]);
  const [releaseQuery, setReleaseQuery] = useState<string | null>(null);
  const [releaseSortKey, setReleaseSortKey] = useState<ReleaseSortKey>("publishDate");
  const [releaseSortDirection, setReleaseSortDirection] = useState<SortDirection>("desc");
  const [selectedEpisodeCoverage, setSelectedEpisodeCoverage] = useState<string | null>(null);
  const [stockFiles, setStockFiles] = useState<StockMediaFile[]>([]);
  const [stockError, setStockError] = useState<string | null>(null);
  const [loadingStock, setLoadingStock] = useState(false);
  const [hasLoadedStock, setHasLoadedStock] = useState(false);
  const [loadingReleases, setLoadingReleases] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [downloading, setDownloading] = useState<string | null>(null);
  const [cancelling, setCancelling] = useState(false);
  const [cancellingJobId, setCancellingJobId] = useState<string | null>(null);
  const [retrying, setRetrying] = useState(false);
  const [updatingStatus, setUpdatingStatus] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [syncing, setSyncing] = useState(false);
  const [editingAssociation, setEditingAssociation] = useState(false);
  const [associationQuery, setAssociationQuery] = useState("");
  const [associationResults, setAssociationResults] = useState<TmdbSearchItem[]>([]);
  const [searchingAssociation, setSearchingAssociation] = useState(false);
  const [updatingAssociation, setUpdatingAssociation] = useState<number | null>(null);
  const [numberingRules, setNumberingRules] = useState<EpisodeNumberingRule[]>([]);
  const [savingNumberingRules, setSavingNumberingRules] = useState(false);
  const requestedReleasesForId = useRef<string | null>(null);
  const requestedEpisodeSyncForId = useRef<string | null>(null);

  function loadMedia(mediaId: string) {
    return api.getMedia(mediaId).then(setMedia).catch((e) => setError((e as Error).message));
  }

  function loadMediaJobs(mediaId: string) {
    return api.getMediaJobs(mediaId).then(setMediaJobs).catch((e) => setError((e as Error).message));
  }

  async function loadStock(mediaId: string) {
    setLoadingStock(true);
    setStockError(null);
    try {
      setStockFiles(await api.getMediaStock(mediaId));
      await loadMedia(mediaId);
    } catch (e) {
      setStockError((e as Error).message);
    } finally {
      setLoadingStock(false);
      setHasLoadedStock(true);
    }
  }

  async function loadReleases(mediaId: string, forceRefresh = false, coverage?: string | null) {
    setLoadingReleases(true);
    try {
      const response = await api.getMediaReleases(mediaId, forceRefresh, coverage);
      setReleases(response.releases);
      setReleaseQuery(response.query);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoadingReleases(false);
    }
  }

  useEffect(() => {
    if (!id) return;
    loadMedia(id);
    loadMediaJobs(id);
    api.getEpisodeNumberingRules(id).then(setNumberingRules).catch((e) => setError((e as Error).message));
    setStockFiles([]);
    setReleaseQuery(null);
    setSelectedEpisodeCoverage(null);
    setStockError(null);
    setLoadingStock(false);
    setHasLoadedStock(false);
    if (requestedReleasesForId.current === id) return;
    requestedReleasesForId.current = id;
    void loadReleases(id);
  }, [id]);

  useEffect(() => {
    if (!id) return;
    const poll = setInterval(() => {
      loadMedia(id);
      loadMediaJobs(id);
    }, 2000);
    return () => clearInterval(poll);
  }, [id]);

  useEffect(() => {
    if (!id || media?.id !== id || media.type === "Movie" || !media.tmdbId ||
      media.seasons.some((season) => season.episodes.length > 0) || requestedEpisodeSyncForId.current === id) return;
    requestedEpisodeSyncForId.current = id;
    void syncEpisodes();
  }, [id, media]);

  if (error) return <p className="error">Erreur : {error}</p>;
  if (!media) return <p>Chargement…</p>;

  const isSeries = media.type !== "Movie";
  const activeJobs = mediaJobs.filter((job) =>
    job.status === "Queued" || job.status === "Running" || job.status === "WaitingForSelection",
  );
  const latestFinishedJob = mediaJobs.find((job) =>
    job.status === "Completed" || job.status === "Cancelled" || job.status === "Failed",
  );
  const retryableJob = latestFinishedJob?.status === "Cancelled" || latestFinishedJob?.status === "Failed"
    ? latestFinishedJob
    : undefined;
  const displayedJobs = activeJobs.length > 0 ? activeJobs : media.inStock || !retryableJob ? [] : [retryableJob];
  const filteredReleases = !selectedEpisodeCoverage
    ? releases
    : releases.filter((release) => release.coverage === selectedEpisodeCoverage);
  const sortedReleases = [...filteredReleases].sort((left, right) => {
    const leftValue = left[releaseSortKey];
    const rightValue = right[releaseSortKey];
    const leftEmpty = leftValue === null || leftValue === undefined || leftValue === "";
    const rightEmpty = rightValue === null || rightValue === undefined || rightValue === "";

    if (leftEmpty || rightEmpty) {
      if (leftEmpty && rightEmpty) return 0;
      return leftEmpty ? 1 : -1;
    }

    const comparison = releaseSortKey === "publishDate"
      ? new Date(leftValue as string).getTime() - new Date(rightValue as string).getTime()
      : typeof leftValue === "string"
        ? leftValue.localeCompare(rightValue as string, "fr", { sensitivity: "base" })
        : Number(leftValue) - Number(rightValue);
    return releaseSortDirection === "asc" ? comparison : -comparison;
  });

  function sortReleases(key: ReleaseSortKey) {
    if (key === releaseSortKey) {
      setReleaseSortDirection((direction) => direction === "asc" ? "desc" : "asc");
      return;
    }

    setReleaseSortKey(key);
    setReleaseSortDirection(key === "title" || key === "coverage" || key === "resolution" || key === "videoCodec" || key === "audioCodec" || key === "source" ? "asc" : "desc");
  }

  async function download(r: ReleaseDto) {
    if (!id || !r.downloadUrl) return;
    setDownloading(r.title);
    try {
      await api.downloadRelease(id, r.downloadUrl, r.title, r.coverage);
      navigate("/jobs");
    } catch (e) {
      setError((e as Error).message);
      setDownloading(null);
    }
  }

  async function cancelProcessing() {
    if (!id) return;
    setCancelling(true);
    setError(null);
    try {
      await api.cancelMediaProcessing(id);
      await Promise.all([loadMedia(id), loadMediaJobs(id)]);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setCancelling(false);
    }
  }

  async function cancelJob(jobId: string) {
    if (!id) return;
    setCancellingJobId(jobId);
    setError(null);
    try {
      await api.cancelJob(jobId);
      await Promise.all([loadMedia(id), loadMediaJobs(id)]);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setCancellingJobId(null);
    }
  }

  async function retryJob(jobId: string) {
    if (!id) return;
    setRetrying(true);
    setError(null);
    try {
      await api.retryJob(jobId);
      await Promise.all([loadMedia(id), loadMediaJobs(id)]);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setRetrying(false);
    }
  }

  async function updateStatus(status: MediaStatus) {
    if (!id || status === media?.status) return;
    setUpdatingStatus(true);
    setError(null);
    try {
      setMedia(await api.updateMediaStatus(id, status));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setUpdatingStatus(false);
    }
  }

  async function syncEpisodes() {
    if (!id) return;
    setSyncing(true);
    try {
      await api.syncEpisodes(id);
      await loadMedia(id);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSyncing(false);
    }
  }

  async function searchAssociation(event: React.FormEvent) {
    event.preventDefault();
    if (!associationQuery.trim()) return;
    setSearchingAssociation(true);
    setError(null);
    try {
      setAssociationResults(await api.searchTmdb(associationQuery));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSearchingAssociation(false);
    }
  }

  async function updateAssociation(result: TmdbSearchItem) {
    if (!id) return;
    setUpdatingAssociation(result.tmdbId);
    setError(null);
    try {
      const updated = await api.updateMediaAssociation(id, result.tmdbId, result.type);
      setMedia(updated);
      if (updated.id !== id) navigate(`/media/${updated.id}`);
      setAssociationResults([]);
      setEditingAssociation(false);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setUpdatingAssociation(null);
    }
  }

  function addNumberingRule() {
    setNumberingRules((rules) => [...rules, { releaseSeasonNumber: (rules.at(-1)?.releaseSeasonNumber ?? 1) + 1, tmdbSeasonNumber: 1, firstTmdbEpisodeNumber: 1 }]);
  }

  async function deleteFromWishlist() {
    if (!id || !media || media.inStock || deleting) return;
    if (!window.confirm(`Supprimer "${media.title}" de la wishlist et retirer ses traitements ?`)) return;
    setDeleting(true);
    setError(null);
    try {
      await api.deleteMedia(id);
      navigate("/");
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setDeleting(false);
    }
  }

  function updateNumberingRule(index: number, key: keyof EpisodeNumberingRule, value: number) {
    setNumberingRules((rules) => rules.map((rule, ruleIndex) => ruleIndex === index ? { ...rule, [key]: value } : rule));
  }

  async function saveNumberingRules() {
    if (!id) return;
    setSavingNumberingRules(true);
    setError(null);
    try {
      setNumberingRules(await api.updateEpisodeNumberingRules(id, numberingRules));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSavingNumberingRules(false);
    }
  }

  return (
    <div>
      <Link to="/" className="back-link">
        ← Bibliothèque
      </Link>

      {isSeries && (
        <section className="association-editor top-numbering-editor">
          <div className="section-head">
            <div>
              <h3>Numérotation des releases</h3>
              <p className="muted">Traduit les saisons et épisodes des torrents vers la numérotation TMDB. Exemple: S02E01 peut devenir S01E14.</p>
            </div>
            <button className="small-btn" onClick={addNumberingRule}>Ajouter une règle</button>
          </div>
          {numberingRules.map((rule, index) => (
            <div className="field-row" key={`${index}-${rule.releaseSeasonNumber}`}>
              <label className="field">
                <span className="field-label">Saison release</span>
                <input type="number" min={1} value={rule.releaseSeasonNumber} onChange={(event) => updateNumberingRule(index, "releaseSeasonNumber", Number(event.target.value))} />
              </label>
              <label className="field">
                <span className="field-label">Saison TMDB</span>
                <input type="number" min={1} value={rule.tmdbSeasonNumber} onChange={(event) => updateNumberingRule(index, "tmdbSeasonNumber", Number(event.target.value))} />
              </label>
              <label className="field">
                <span className="field-label">Premier épisode TMDB</span>
                <input type="number" min={1} value={rule.firstTmdbEpisodeNumber} onChange={(event) => updateNumberingRule(index, "firstTmdbEpisodeNumber", Number(event.target.value))} />
              </label>
              <button className="ghost small-btn" onClick={() => setNumberingRules((rules) => rules.filter((_, ruleIndex) => ruleIndex !== index))}>Supprimer</button>
            </div>
          ))}
          <button className="small-btn" onClick={saveNumberingRules} disabled={savingNumberingRules}>{savingNumberingRules ? "Enregistrement…" : "Enregistrer les règles"}</button>
        </section>
      )}

      <div className="detail-header">
        {media.posterPath ? (
          <img src={`${TMDB_IMAGE}${media.posterPath}`} alt={media.title} />
        ) : (
          <div className="poster-placeholder" />
        )}
        <div>
          <h2>
            {media.title} {media.year ? <span className="muted">({media.year})</span> : null}
          </h2>
          <p className={`media-state status-${media.inStock ? "available" : "wanted"}`}>{media.inStock ? "Dans le stock" : "Wishlist"}</p>
          {media.pipelineStage && displayedJobs.length === 0 && <p className="media-state status-processing">{stateLabel(media)}</p>}
          {!media.pipelineStage && !media.inStock && (
            <label className="media-status-control">
              Suivi manuel
              <select
                value={media.status}
                disabled={updatingStatus}
                onChange={(event) => updateStatus(event.target.value as MediaStatus)}
              >
                <option value="Wanted">Wishlist</option>
                <option value="Ignored">Ignoré</option>
              </select>
            </label>
          )}
          <p className="muted">{media.type}</p>
          {media.storageName && (
            <p className="source-name">
              <span className="muted">Nom détecté sur le stockage</span>
              <code>{media.storageName}</code>
            </p>
          )}
          {media.overview && <p className="overview">{media.overview}</p>}
          {displayedJobs.map((job) => {
            const remainingTime = formatRemainingTime(job.estimatedRemainingSeconds);
            const episodeLabel = job.seasonNumber !== null && job.seasonNumber !== undefined &&
              job.episodeNumber !== null && job.episodeNumber !== undefined
              ? ` · Saison ${job.seasonNumber} · Épisode ${job.episodeNumber}`
              : "";
            return (
              <section key={job.id} className="media-job-status">
                <div className="media-job-heading">
                  <div>
                    <span className="media-job-kicker">Pipeline{episodeLabel}</span>
                    <strong>{JOB_STAGE_LABELS[job.kind]}{job.episodeTitle ? ` · ${job.episodeTitle}` : ""}</strong>
                  </div>
                  <span className={`badge status-${job.status.toLowerCase()}`}>
                    {JOB_STATUS_LABELS[job.status]}
                  </span>
                </div>
                <div className="progress media-job-progress">
                  <div
                    className={`progress-bar${job.status === "Running" ? " active" : ""}${
                      job.status === "Failed" ? " failed" : ""
                    }`}
                    style={{ width: `${job.progress}%` }}
                  />
                  <span className="progress-label">
                    {job.kind === "Encode" ? `HandBrake · ${formatProgress(job.progress)}` : formatProgress(job.progress)}
                  </span>
                </div>
                {(job.kind === "Encode" || remainingTime) && (
                  <div className="media-job-metrics">
                    {job.kind === "Encode" && job.status === "Running" && job.encodingFps !== null && job.encodingFps !== undefined && (
                      <div>
                        <span>Vitesse</span>
                        <strong>{formatEncodingSpeed(job.encodingFps)}</strong>
                      </div>
                    )}
                    {remainingTime && (
                      <div>
                        <span>Temps restant</span>
                        <strong>{remainingTime}</strong>
                      </div>
                    )}
                  </div>
                )}
                {job.error && <p className="error small">{job.error}</p>}
                <div className="media-job-actions">
                  {job.kind === "TrackSelection" && job.status === "WaitingForSelection" && (
                    <Link className="small-btn" to="/jobs">Choisir les pistes</Link>
                  )}
                  {job.status === "Running" && (
                    <button className="ghost small-btn" onClick={() => cancelJob(job.id)} disabled={cancellingJobId === job.id}>
                      {cancellingJobId === job.id ? "Annulation…" : "Annuler cet épisode"}
                    </button>
                  )}
                  {(job.status === "Cancelled" || job.status === "Failed") && (
                    <button className="small-btn" onClick={() => retryJob(job.id)} disabled={retrying}>
                      {retrying ? "Relance…" : "Relancer cette étape"}
                    </button>
                  )}
                </div>
              </section>
            );
          })}
          {media.status === "Processing" && displayedJobs.length === 0 && (
            <button className="ghost" onClick={cancelProcessing} disabled={cancelling}>
              {cancelling ? "Annulation…" : "Annuler le traitement"}
            </button>
          )}
          <button
            className="ghost association-toggle"
            onClick={() => {
              setEditingAssociation((open) => !open);
              setAssociationQuery(media.title);
              setAssociationResults([]);
            }}
          >
            Corriger l'association TMDB
          </button>
          {!media.inStock && (
            <button className="ghost" onClick={deleteFromWishlist} disabled={deleting}
              title="Supprimer de la wishlist">
              <Trash2 size={16} aria-hidden="true" /> {deleting ? "Suppression…" : "Supprimer de la wishlist"}
            </button>
          )}
        </div>
      </div>

      {editingAssociation && (
        <section className="association-editor">
          <h3>Choisir le bon média</h3>
          <form className="search-bar" onSubmit={searchAssociation}>
            <input
              value={associationQuery}
              onChange={(event) => setAssociationQuery(event.target.value)}
              placeholder="Titre du film, de la série ou de l'animé"
              autoFocus
            />
            <button type="submit" disabled={searchingAssociation}>
              {searchingAssociation ? "Recherche…" : "Rechercher"}
            </button>
          </form>
          {associationResults.length > 0 && (
            <div className="association-results">
              {associationResults.map((result) => (
                <article key={`${result.type}-${result.tmdbId}`} className="association-result">
                  {result.posterPath ? (
                    <img src={`${TMDB_IMAGE}${result.posterPath}`} alt="" />
                  ) : (
                    <div className="poster-placeholder" />
                  )}
                  <div>
                    <strong>{result.title}</strong>
                    <span className="muted">
                      {result.type} {result.year ? `· ${result.year}` : ""}
                    </span>
                    {result.overview && <p className="muted">{result.overview}</p>}
                  </div>
                  <button
                    className="small-btn"
                    disabled={updatingAssociation !== null}
                    onClick={() => updateAssociation(result)}
                  >
                    {updatingAssociation === result.tmdbId ? "Association…" : "Choisir"}
                  </button>
                </article>
              ))}
            </div>
          )}
        </section>
      )}

      {isSeries && (
        <div className="series-episodes-layout">
          <section className="episodes">
            <div className="section-head">
              <h3>Épisodes</h3>
              <button className="small-btn" onClick={syncEpisodes} disabled={syncing}>
                {syncing ? "Synchronisation…" : "Synchroniser les épisodes"}
              </button>
            </div>
            {media.seasons.length === 0 ? (
              <p className="muted">{syncing ? "Chargement des épisodes depuis TMDB…" : "Aucun épisode connu. Synchronise les épisodes pour récupérer la liste TMDB."}</p>
            ) : (
              media.seasons.map((s) => {
                const have = s.episodes.filter((e) => e.status === "Available").length;
                const missing = s.episodes.filter((e) => e.status === "Missing").length;
                return (
                  <div key={s.id} className="season">
                    <div className="season-title">
                      <strong>Saison {s.seasonNumber}</strong>
                      <span className="muted">
                        {have}/{s.episodes.length} présents
                        {missing > 0 ? ` · ${missing} manquant${missing > 1 ? "s" : ""}` : ""}
                      </span>
                    </div>
                    <div className="episode-grid">
                      {s.episodes.map((e) => {
                        const coverage = `S${String(s.seasonNumber).padStart(2, "0")}E${String(e.episodeNumber).padStart(2, "0")}`;
                        const isSelected = selectedEpisodeCoverage === coverage;
                        return (
                          <button
                            key={e.id}
                            type="button"
                            className={`ep ep-${e.status.toLowerCase()}${isSelected ? " active" : ""}`}
                            title={`${coverage}${e.title ? " — " + e.title : ""} (${e.status})`}
                            aria-pressed={isSelected}
                            aria-label={`Filtrer les torrents pour ${coverage}${e.title ? `, ${e.title}` : ""}`}
                            onClick={() => {
                              const nextCoverage = selectedEpisodeCoverage === coverage ? null : coverage;
                              setSelectedEpisodeCoverage(nextCoverage);
                              if (id) void loadReleases(id, true, nextCoverage);
                            }}
                          >
                            {e.episodeNumber}
                          </button>
                        );
                      })}
                    </div>
                  </div>
                );
              })
            )}
            <div className="ep-legend muted">
              <span className="ep ep-available" /> présent
              <span className="ep ep-missing" /> manquant
              <span className="ep ep-unaired" /> à venir
              <span className="ep ep-processing" /> en cours
            </div>
          </section>

        </div>
      )}

      <div className="section-head">
        <h3>Stock proposé</h3>
        <button className="small-btn" onClick={() => id && loadStock(id)} disabled={loadingStock}>
          {loadingStock ? "Analyse…" : "Analyser le stock"}
        </button>
      </div>
      {loadingStock ? (
        <p>Analyse du stock…</p>
      ) : stockError ? (
        <p className="muted">Stock indisponible : {stockError}</p>
      ) : !hasLoadedStock ? (
        <p className="muted">Métadonnées techniques non analysées.</p>
      ) : stockFiles.length === 0 ? (
        <p className="muted">Aucun fichier de stock associé à ce média.</p>
      ) : (
        <table className="table stock-files">
          <thead>
            <tr>
              <th>Fichier</th>
              <th>Durée</th>
              <th>Rés.</th>
              <th>Vidéo</th>
              <th>Débit</th>
              <th>Audio</th>
              <th>Sous-titres</th>
              <th>Taille</th>
            </tr>
          </thead>
          <tbody>
            {stockFiles.map((file) => (
              <tr key={file.name}>
                <td className="release-name" title={file.name}>
                  {file.name}
                  {file.isCloud && <span className="muted">MEGA · Métadonnées disponibles avec une copie locale</span>}
                </td>
                <td className="nowrap">{formatDuration(file.duration)}</td>
                <td>{formatResolution(file.width, file.height)}</td>
                <td>{file.videoCodec ? `${file.videoCodec}${file.bitDepth ? ` ${file.bitDepth}-bit` : ""}` : "—"}</td>
                <td className="nowrap">{formatBitRate(file.videoBitRate)}</td>
                <td><AudioTrackList tracks={file.audioTracks} /></td>
                <td><SubtitleTrackList tracks={file.subtitleTracks} /></td>
                <td>{formatSize(file.sizeBytes)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <div className="section-head">
        <div>
          <h3>Torrents proposés</h3>
          {releaseQuery && <p className="muted">Recherche C411 : <code>{releaseQuery}</code></p>}
          {selectedEpisodeCoverage && <p className="muted">Épisode sélectionné : <code>{selectedEpisodeCoverage}</code></p>}
        </div>
        <button className="small-btn" type="button" onClick={() => id && loadReleases(id, true)} disabled={loadingReleases}>
          <RefreshCw size={15} aria-hidden="true" />
          {loadingReleases ? "Actualisation…" : "Actualiser les torrents"}
        </button>
      </div>
      {loadingReleases ? (
        <p>Recherche des sources…</p>
      ) : releases.length === 0 ? (
        <p className="muted">Aucun torrent trouvé.</p>
      ) : (
        <table className="table releases">
          <thead>
            <tr>
              {RELEASE_SORT_COLUMNS.map((column) => {
                const isSorted = releaseSortKey === column.key;
                const SortIcon = isSorted
                  ? releaseSortDirection === "asc" ? ArrowUp : ArrowDown
                  : ArrowUpDown;
                return (
                  <th key={column.key} aria-sort={isSorted ? (releaseSortDirection === "asc" ? "ascending" : "descending") : "none"}>
                    <button className="table-sort" type="button" onClick={() => sortReleases(column.key)}>
                      {column.label} <SortIcon size={14} aria-hidden="true" />
                    </button>
                  </th>
                );
              })}
              <th></th>
            </tr>
          </thead>
          <tbody>
            {sortedReleases.map((r, i) => (
              <tr key={i} className={r.accepted ? "accepted" : "rejected"}>
                <td className="release-name" title={r.title}>
                  {r.freeleech && <span className="tag fl">FL</span>} {r.title}
                </td>
                <td className="nowrap">{r.coverage}</td>
                <td className="muted nowrap">{formatDate(r.publishDate)}</td>
                <td>{r.resolution ?? "—"}</td>
                <td className="nowrap">
                  {r.videoCodec ?? "—"}
                  {r.dynamicRange && <span className="tag hdr">{r.dynamicRange}</span>}
                  {r.bitDepth ? <span className="tag">{r.bitDepth}bit</span> : null}
                </td>
                <td className="nowrap">
                  {r.audioCodec ? `${r.audioCodec}${r.audioChannels ? " " + r.audioChannels : ""}` : "—"}
                </td>
                <td>{r.source ?? "—"}</td>
                <td>{r.isMulti ? "✓" : "—"}</td>
                <td>{formatSize(r.sizeBytes)}</td>
                <td>
                  <span className="seeders" title={`${r.grabs} téléchargements (grabs)`}>
                    <span className="seed-dot" /> {r.seeders}
                    {r.grabs ? <span className="grabs">↓{r.grabs}</span> : null}
                  </span>
                </td>
                <td>{r.accepted ? r.score : "—"}</td>
                <td>
                  {r.accepted ? (
                    <span className="badge status-available">Accepté</span>
                  ) : (
                    <span className="badge status-failed" title={r.rejectionReasons.join(" ; ")}>
                      Rejeté
                    </span>
                  )}
                </td>
                <td>
                  <button
                    className="small-btn"
                    disabled={!r.downloadUrl || downloading !== null}
                    onClick={() => download(r)}
                  >
                    {downloading === r.title ? "…" : "Télécharger"}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

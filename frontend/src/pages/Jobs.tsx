import { useDeferredValue, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api } from "../api";
import type { JobDto, TrackSelection } from "../types";

function formatSize(bytes: number): string {
  if (!bytes) return "";
  const gb = bytes / 1024 ** 3;
  if (gb >= 1) return `${gb.toFixed(2)} Go`;
  return `${(bytes / 1024 ** 2).toFixed(0)} Mo`;
}

function formatSpeed(bytesPerSec: number): string {
  if (!bytesPerSec) return "0 Ko/s";
  const mb = bytesPerSec / 1024 ** 2;
  if (mb >= 1) return `${mb.toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} Mo/s`;
  return `${(bytesPerSec / 1024).toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} Ko/s`;
}

function formatEncodingSpeed(framesPerSecond: number): string {
  return `${framesPerSecond.toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} fps`;
}

function formatProgress(progress: number): string {
  return `${progress.toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} %`;
}

function formatRemainingTime(seconds?: number | null): string {
  if (seconds === null || seconds === undefined) return "—";
  const totalSeconds = Math.max(0, Math.ceil(seconds));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const remainingSeconds = totalSeconds % 60;
  if (hours > 0) return `~ ${hours} h ${String(minutes).padStart(2, "0")} min`;
  if (minutes > 0) return `~ ${minutes} min ${String(remainingSeconds).padStart(2, "0")} s`;
  return `~ ${remainingSeconds} s`;
}

export default function Jobs() {
  const pageSize = 50;
  const [jobs, setJobs] = useState<JobDto[]>([]);
  const [page, setPage] = useState(1);
  const [totalCount, setTotalCount] = useState(0);
  const [search, setSearch] = useState("");
  const [kind, setKind] = useState("");
  const [status, setStatus] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [selection, setSelection] = useState<TrackSelection | null>(null);
  const [audioIndexes, setAudioIndexes] = useState<number[]>([]);
  const [subtitleIndexes, setSubtitleIndexes] = useState<number[]>([]);
  const [loadingSelection, setLoadingSelection] = useState<string | null>(null);
  const [confirmingSelection, setConfirmingSelection] = useState(false);
  const [confirmingMatchingSelections, setConfirmingMatchingSelections] = useState(false);
  const [cancellingSelection, setCancellingSelection] = useState(false);
  const deferredSearch = useDeferredValue(search);

  async function load() {
    try {
      const result = await api.getJobsPage(page, pageSize, { search: deferredSearch, kind, status });
      setJobs(result.jobs);
      setTotalCount(result.totalCount);
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  useEffect(() => {
    load();
    const t = setInterval(load, 2000);
    return () => clearInterval(t);
  }, [page, deferredSearch, kind, status]);

  async function cancel(id: string) {
    try {
      await api.cancelJob(id);
      await load();
    } catch (e) {
      setError((e as Error).message);
    }
  }

  async function retry(id: string) {
    try {
      await api.retryJob(id);
      await load();
    } catch (e) {
      setError((e as Error).message);
    }
  }

  async function openTrackSelection(id: string) {
    setLoadingSelection(id);
    setError(null);
    try {
      const value = await api.getTrackSelection(id);
      setSelection(value);
      setAudioIndexes(value.recommendedAudioTrackIndexes);
      setSubtitleIndexes(value.recommendedSubtitleTrackIndexes);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoadingSelection(null);
    }
  }

  function toggle(index: number, values: number[], setValues: (next: number[]) => void) {
    setValues(values.includes(index) ? values.filter((value) => value !== index) : [...values, index]);
  }

  async function confirmSelection() {
    if (!selection) return;
    setConfirmingSelection(true);
    setError(null);
    try {
      await api.confirmTrackSelection(selection.jobId, audioIndexes, subtitleIndexes);
      setSelection(null);
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setConfirmingSelection(false);
    }
  }

  async function confirmMatchingSelections() {
    if (!selection) return;
    setConfirmingMatchingSelections(true);
    setError(null);
    try {
      const result = await api.confirmMatchingTrackSelections(selection.jobId, audioIndexes, subtitleIndexes);
      setSelection(null);
      await load();
      if (result.appliedCount === 0) setError("Aucun autre épisode n'a les mêmes pistes.");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setConfirmingMatchingSelections(false);
    }
  }

  async function cancelSelection() {
    if (!selection) return;
    setCancellingSelection(true);
    setError(null);
    try {
      await api.cancelJob(selection.jobId);
      setSelection(null);
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setCancellingSelection(false);
    }
  }

  if (error) return <p className="error">Erreur : {error}</p>;
  if (jobs.length === 0 && totalCount === 0 && !search && !kind && !status) return <p>Aucun job.</p>;

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

  return (
    <>
      {selection && (
        <section className="track-selection">
          <div className="section-head">
            <div>
              <h2>{selection.mediaTitle ?? "Média"}</h2>
              <p className="muted">Sélection recommandée précochée. Modifie-la ou confirme pour poursuivre l'encodage.</p>
            </div>
            <button className="ghost small-btn" onClick={() => setSelection(null)}>Fermer</button>
          </div>
          {selection.missingRequirements.length > 0 && (
            <div className="track-requirements" role="alert">
              <strong>Éléments absents</strong>
              <span>{selection.missingRequirements.join(" · ")}</span>
            </div>
          )}
          <div className="track-selection-columns">
            <div>
              <h3>Audio</h3>
              {selection.audioTracks.map((track) => (
                <label key={track.index} className={`track-option${track.isAudioDescription ? " unavailable" : ""}${selection.recommendedAudioTrackIndexes.includes(track.index) ? " recommended" : ""}`}>
                  <input
                    type="checkbox"
                    checked={audioIndexes.includes(track.index)}
                    disabled={track.isAudioDescription}
                    onChange={() => toggle(track.index, audioIndexes, setAudioIndexes)}
                  />
                  <span><strong>Piste {track.index} · {track.language?.toUpperCase() ?? "?"}</strong> {[track.codec, track.channels, track.name].filter(Boolean).join(" · ")}</span>
                  {selection.recommendedAudioTrackIndexes.includes(track.index) && <span className="track-flag">Recommandée</span>}
                  {track.isAudioDescription && <span className="track-flag">AD exclue</span>}
                </label>
              ))}
            </div>
            <div>
              <h3>Sous-titres</h3>
              {selection.subtitleTracks.map((track) => (
                  <label key={track.index} className={`track-option${track.isSdh ? " unavailable" : ""}${selection.recommendedSubtitleTrackIndexes.includes(track.index) ? " recommended" : ""}`}>
                  <input
                    type="checkbox"
                    checked={subtitleIndexes.includes(track.index)}
                      disabled={track.isSdh}
                    onChange={() => toggle(track.index, subtitleIndexes, setSubtitleIndexes)}
                  />
                  <span><strong>Piste {track.index} · {track.language?.toUpperCase() ?? "?"}</strong> {[track.format, track.source].filter(Boolean).join(" · ")}</span>
                  {selection.recommendedSubtitleTrackIndexes.includes(track.index) && <span className="track-flag">Recommandée</span>}
                    {track.isSdh && <span className="track-flag">SDH exclu</span>}
                  {track.isForced && <span className="track-flag">Forcé</span>}
                </label>
              ))}
            </div>
          </div>
          <div className="track-selection-actions">
            <button onClick={confirmSelection} disabled={confirmingSelection || confirmingMatchingSelections || cancellingSelection || audioIndexes.length === 0}>
              {confirmingSelection ? "Validation…" : "Valider et encoder"}
            </button>
            <button className="ghost" onClick={confirmMatchingSelections} disabled={confirmingSelection || confirmingMatchingSelections || cancellingSelection || audioIndexes.length === 0}>
              {confirmingMatchingSelections ? "Validation groupée…" : "Valider pour les pistes identiques"}
            </button>
            <button className="ghost" onClick={cancelSelection} disabled={confirmingSelection || confirmingMatchingSelections || cancellingSelection}>
              {cancellingSelection ? "Annulation…" : "Annuler le traitement"}
            </button>
          </div>
        </section>
      )}
      <table className="table">
      <thead>
        <tr>
          <th>Média</th>
          <th>Étape</th>
          <th>Statut</th>
          <th>Progression</th>
          <th>Vitesse</th>
          <th>Temps restant</th>
          <th>Seeds</th>
          <th>Créé</th>
          <th></th>
        </tr>
        <tr className="jobs-column-filters">
          <th>
            <input
              value={search}
              onChange={(event) => { setSearch(event.target.value); setPage(1); }}
              placeholder="Filtrer le média"
              aria-label="Filtrer par média"
            />
          </th>
          <th>
            <select value={kind} onChange={(event) => { setKind(event.target.value); setPage(1); }} aria-label="Filtrer par étape">
              <option value="">Toutes les étapes</option>
              <option value="Search">Recherche</option>
              <option value="Download">Téléchargement</option>
              <option value="TrackSelection">Choix des pistes</option>
              <option value="Encode">Encodage</option>
              <option value="Upload">Upload</option>
              <option value="Notify">Notification</option>
              <option value="Scan">Scan</option>
              <option value="MetadataRefresh">Métadonnées</option>
            </select>
          </th>
          <th>
            <select value={status} onChange={(event) => { setStatus(event.target.value); setPage(1); }} aria-label="Filtrer par statut">
              <option value="">Tous les statuts</option>
              <option value="Queued">En attente</option>
              <option value="Running">En cours</option>
              <option value="WaitingForSelection">Choix requis</option>
              <option value="Completed">Terminé</option>
              <option value="Failed">Échoué</option>
              <option value="Cancelled">Annulé</option>
            </select>
          </th>
          <th colSpan={6} />
        </tr>
      </thead>
      <tbody>
        {jobs.length === 0 && (
          <tr><td colSpan={9} className="muted">Aucun job ne correspond aux filtres.</td></tr>
        )}
        {jobs.map((j) => {
          const active = j.status === "Running";
          const isDownload = j.kind === "Download";
          const isEncoding = j.kind === "Encode";
          const cancellable = j.status === "Queued" || j.status === "Running" || j.status === "WaitingForSelection";
          const retryable = j.status === "Cancelled" || j.status === "Failed";
          return (
            <tr key={j.id}>
              <td>
                {j.mediaItemId ? (
                  <>
                    <Link to={`/media/${j.mediaItemId}`} className="job-media-link">
                      {j.mediaTitle ?? "Média"}
                    </Link>
                    {j.seasonNumber !== null && j.seasonNumber !== undefined &&
                      j.episodeNumber !== null && j.episodeNumber !== undefined && (
                      <div className="muted">
                        Saison {j.seasonNumber} · Épisode {j.episodeNumber}
                        {j.episodeTitle ? ` — ${j.episodeTitle}` : ""}
                      </div>
                    )}
                  </>
                ) : (
                  j.mediaTitle ?? "—"
                )}
              </td>
              <td>{j.kind}</td>
              <td>
                <span className={`badge status-${j.status.toLowerCase()}`}>{j.status}</span>
                {j.error && (
                  <div className="error small" title={j.error}>
                    {j.error}
                  </div>
                )}
                {j.cloudPath && (
                  <code className="cloud-path" title={j.cloudPath}>
                    MEGA: {j.cloudPath}
                  </code>
                )}
              </td>
              <td>
                <div className="progress">
                  <div
                    className={`progress-bar${active ? " active" : ""}${
                      j.status === "Failed" ? " failed" : ""
                    }`}
                    style={{ width: `${j.progress}%` }}
                  />
                  <span className="progress-label">
                    {isEncoding ? `HandBrake · ${formatProgress(j.progress)}` : formatProgress(j.progress)}
                    {isDownload && j.sizeBytes ? ` · ${formatSize(j.sizeBytes)}` : ""}
                  </span>
                </div>
              </td>
              <td>
                {isDownload && active ? (
                  <span className="speed">↓ {formatSpeed(j.downloadSpeed)}</span>
                ) : isEncoding && j.encodingFps !== null && j.encodingFps !== undefined ? (
                  <span className="speed">{formatEncodingSpeed(j.encodingFps)}</span>
                ) : (
                  "—"
                )}
              </td>
              <td className="muted">{formatRemainingTime(j.estimatedRemainingSeconds)}</td>
              <td>
                {isDownload ? (
                  <span className="seeders">
                    <span className="seed-dot" /> {j.seeders}
                  </span>
                ) : (
                  "—"
                )}
              </td>
              <td className="muted">{new Date(j.createdAt).toLocaleString()}</td>
              <td>
                {j.kind === "TrackSelection" && j.status === "WaitingForSelection" && (
                  <button className="small-btn" onClick={() => openTrackSelection(j.id)} disabled={loadingSelection === j.id}>
                    {loadingSelection === j.id ? "Analyse…" : "Choisir les pistes"}
                  </button>
                )}
                {cancellable && (
                  <button className="ghost small-btn" onClick={() => cancel(j.id)}>
                    Annuler
                  </button>
                )}
                {retryable && (
                  <button className="small-btn" onClick={() => retry(j.id)}>
                    Relancer ici
                  </button>
                )}
              </td>
            </tr>
          );
        })}
      </tbody>
      </table>
      <nav className="jobs-pagination" aria-label="Pagination des jobs">
        <button className="ghost small-btn" onClick={() => setPage((current) => Math.max(1, current - 1))} disabled={page === 1}>
          Précédent
        </button>
        <span className="muted">Page {page} sur {totalPages} · {totalCount} jobs</span>
        <button className="ghost small-btn" onClick={() => setPage((current) => Math.min(totalPages, current + 1))} disabled={page >= totalPages}>
          Suivant
        </button>
      </nav>
    </>
  );
}

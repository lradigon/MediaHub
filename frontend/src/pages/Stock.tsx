import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { ArrowLeft, ChevronLeft, ChevronRight, HardDrive, RefreshCw, ScanLine, X } from "lucide-react";
import { api, TMDB_IMAGE } from "../api";
import type { StockDetails, StockFileInspection } from "../types";

const states: Record<string, string> = { Pending: "Non analysé", Ready: "Analysé", Failed: "Échec", Queued: "En file", Running: "En cours", Completed: "Terminé", Cancelled: "Annulé" };
const unknown = "Non renseigné";
function size(bytes: number) { return `${(bytes / 1073741824).toLocaleString("fr-FR", { maximumFractionDigits: 2 })} Go`; }
function rate(value?: number | null) { return value ? `${(value / 1000).toLocaleString("fr-FR", { maximumFractionDigits: 1 })} kb/s` : unknown; }
function seconds(value?: string | null) {
  const match = value?.match(/^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/);
  return match ? Number(match[1] ?? 0) * 86400 + Number(match[2]) * 3600 + Number(match[3]) * 60 + Number(match[4]) : null;
}
function duration(value: number | null) {
  if (value == null) return unknown;
  const total = Math.round(value);
  return `${Math.floor(total / 3600)} h ${Math.floor(total % 3600 / 60).toString().padStart(2, "0")} min ${(total % 60).toString().padStart(2, "0")} s`;
}
function date(value?: string | null) { return value ? new Date(value).toLocaleString("fr-FR") : "Jamais"; }
function episode(file: StockFileInspection) {
  const match = file.name.match(/S(\d{1,3})[\s._-]*E(\d{1,4})/i);
  return match ? { season: Number(match[1]), number: Number(match[2]), label: `S${match[1].padStart(2, "0")}E${match[2].padStart(2, "0")}` } : null;
}
function language(value?: string | null) {
  const names: Record<string, string> = { fra: "Français", fre: "Français", fr: "Français", eng: "Anglais", en: "Anglais", jpn: "Japonais", ja: "Japonais", und: "Indéterminé" };
  return value ? names[value.toLowerCase()] ?? value : unknown;
}
function frameRate(value?: string | null) {
  if (!value) return unknown;
  const parts = value.split("/").map(Number);
  const number = parts.length === 2 ? parts[0] / parts[1] : parts[0];
  return Number.isFinite(number) && number > 0 ? `${number.toLocaleString("fr-FR", { maximumFractionDigits: 3 })} images/s` : unknown;
}

function FileDetails({ file }: { file: StockFileInspection }) {
  const details = file.details;
  if (!details) return <p className="muted">{file.error ?? "Métadonnées non analysées."}</p>;
  const video: [string, string | number | null | undefined][] = [
    ["Conteneur", details.container], ["Durée", duration(seconds(details.duration))],
    ["Résolution", details.width && details.height ? `${details.width} x ${details.height}` : null],
    ["Codec vidéo", details.videoCodec], ["Profil vidéo", details.videoProfile],
    ["Profondeur", details.bitDepth ? `${details.bitDepth} bits` : null], ["Format de pixels", details.pixelFormat],
    ["Fréquence moyenne", frameRate(details.frameRate)], ["Ratio", details.displayAspectRatio],
    ["Débit vidéo déclaré", rate(details.videoBitRate)], ["Débit global déclaré", rate(details.overallBitRate)],
    ["HDR", details.hdr], ["Primaires", details.colorPrimaries], ["Transfert", details.colorTransfer], ["Espace couleur", details.colorSpace],
    ["Modification", date(file.modifiedAt)], ["Dernière analyse", date(file.inspectedAt)]
  ];
  return <div className="stock-file-details">
    {file.status !== "Ready" && <p className="stock-warning">Résultat précédent du {date(file.inspectedAt)}{file.error ? ` : ${file.error}` : ""}</p>}
    <h3>Fichier et vidéo</h3>
    <dl className="stock-metadata">{video.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value ?? unknown}</dd></div>)}</dl>
    <h3>Audio <span className="muted">{details.audioTracks.length} piste(s)</span></h3>
    {details.audioTracks.length ? <div className="stock-track-scroll"><table className="table stock-tracks">
      <thead><tr><th>Piste</th><th>Langue / titre</th><th>Codec / profil</th><th>Canaux</th><th>Débit déclaré</th><th>Échantillonnage</th><th>Indicateurs</th></tr></thead>
      <tbody>{details.audioTracks.map((track, index) => <tr key={index}>
        <td>#{track.index ?? index + 1}</td><td>{language(track.language)}{track.name && <small>{track.name}</small>}</td>
        <td>{track.codec ?? unknown}{track.profile && <small>{track.profile}</small>}</td>
        <td>{track.channels ?? track.channelCount ?? unknown}</td><td>{rate(track.bitRate)}</td>
        <td>{track.sampleRate ? `${track.sampleRate.toLocaleString("fr-FR")} Hz` : unknown}</td>
        <td>{[track.isDefault && "Par défaut", track.isForced && "Forcée", track.isCommentary && "Commentaire", track.isAudioDescription && "Audiodescription"].filter(Boolean).join(", ") || "Aucun"}</td>
      </tr>)}</tbody>
    </table></div> : <p className="muted">Aucune piste audio.</p>}
    <h3>Sous-titres <span className="muted">{details.subtitleTracks.length} piste(s)</span></h3>
    {details.subtitleTracks.length ? <div className="stock-track-scroll"><table className="table stock-tracks">
      <thead><tr><th>Piste</th><th>Langue</th><th>Format</th><th>Titre</th><th>Indicateurs</th></tr></thead>
      <tbody>{details.subtitleTracks.map((track, index) => <tr key={index}><td>#{index + 1}</td><td>{language(track.language)}</td>
        <td>{track.format ?? unknown}</td><td>{track.source ?? unknown}</td>
        <td>{[track.isDefault && "Par défaut", track.isForced && "Forcés", track.source?.toUpperCase().includes("SDH") && "SDH"].filter(Boolean).join(", ") || "Aucun"}</td>
      </tr>)}</tbody>
    </table></div> : <p className="muted">Aucune piste de sous-titres intégrée.</p>}
    {!!details.externalSubtitles?.length && <><h3>Sous-titres externes</h3><ul>{details.externalSubtitles.map(name => <li key={name}>{name}</li>)}</ul></>}
  </div>;
}

export default function Stock() {
  const { id } = useParams<{ id: string }>();
  const [data, setData] = useState<StockDetails | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [query, setQuery] = useState("");
  const [season, setSeason] = useState("");
  const [state, setState] = useState("");
  const [sort, setSort] = useState("episode");
  const [page, setPage] = useState(1);
  useEffect(() => {
    if (!id) return;
    setData(current => current?.mediaId === id ? current : null);
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    async function load() {
      try {
        const result = await api.getStockDetails(id!, controller.signal);
        if (controller.signal.aborted) return;
        setData(result);
        setError("");
        if (result.inspection?.status === "Queued" || result.inspection?.status === "Running") timer = setTimeout(load, 2000);
      } catch (exception) {
        if (!controller.signal.aborted) {
          setError(exception instanceof Error ? exception.message : "Stock indisponible.");
          timer = setTimeout(load, 5000);
        }
      }
    }
    void load();
    return () => { controller.abort(); clearTimeout(timer); };
  }, [id, refresh]);
  async function command(options?: { maxFiles?: number; force?: boolean; fileId?: string }, cancel = false) {
    if (!id) return;
    setBusy(true);
    try {
      if (cancel) await api.cancelStockInspection(id);
      else await api.inspectStock(id, options);
      setRefresh(value => value + 1);
      setError("");
    } catch (exception) { setError(exception instanceof Error ? exception.message : "Opération impossible."); }
    finally { setBusy(false); }
  }
  const active = data?.inspection?.status === "Queued" || data?.inspection?.status === "Running";
  const present = data?.files.filter(file => file.isPresent) ?? [];
  const analysed = present.filter(file => file.status === "Ready" && file.details);
  const hasInventory = present.length > 0 || data?.inspection?.status === "Completed";
  const seasons = [...new Set(present.map(file => episode(file)?.season).filter(value => value != null))].sort((first, second) => first! - second!);
  const files = (data?.files ?? []).filter(file => (state === "Missing" ? !file.isPresent : file.isPresent) &&
    (!query || file.name.toLowerCase().includes(query.toLowerCase())) && (!season || String(episode(file)?.season) === season) &&
    (!state || state === "Missing" || file.status === state)).sort((first, second) => {
      if (sort === "size") return second.sizeBytes - first.sizeBytes;
      if (sort === "duration") return (seconds(second.details?.duration) ?? -1) - (seconds(first.details?.duration) ?? -1);
      if (sort === "bitrate") return (second.details?.videoBitRate ?? -1) - (first.details?.videoBitRate ?? -1);
      return first.name.localeCompare(second.name, "fr", { numeric: true });
    });
  const pageCount = Math.max(1, Math.ceil(files.length / 25));
  const currentPage = Math.min(page, pageCount);
  const durations = analysed.map(file => seconds(file.details?.duration)).filter((value): value is number => value != null);
  const knownDuration = durations.reduce((total, value) => total + value, 0);
  const scan = data?.inspection;
  return <section className="stock-page">
    <Link className="stock-back" to={`/media/${id}`}><ArrowLeft size={16} />Fiche du média</Link>
    <div className="stock-heading">
      {data?.posterPath && <img className="stock-poster" src={`${TMDB_IMAGE}${data.posterPath}`} alt={data.title} />}
      <div><p className="muted">Détails du stock</p><h2>{data?.title ?? "Chargement du stock..."}</h2>
        <div className="stock-totals"><span><HardDrive size={16} />{hasInventory ? `${present.length} fichier(s)` : "Inventaire non disponible"}</span>
          <span>{hasInventory ? size(present.reduce((total, file) => total + file.sizeBytes, 0)) : "Taille inconnue"}</span>
          {hasInventory && <span>{analysed.length}/{present.length} analysés</span>}
          <span>Durée connue : {durations.length ? duration(knownDuration) : unknown}</span></div>
      </div>
    </div>
    {error && <p className="stock-warning" role="alert">{error}</p>}
    <div className="stock-actions">
      <button disabled={!data?.hasStorage || busy || active} onClick={() => void command({ maxFiles: 1 })}><ScanLine size={16} />Analyser un fichier</button>
      <button className="ghost" disabled={!data?.hasStorage || busy || active} onClick={() => void command()}><RefreshCw size={16} />Analyser les fichiers nouveaux ou modifiés</button>
      <button className="ghost stock-icon" aria-label="Actualiser les résultats" title="Actualiser les résultats" disabled={busy} onClick={() => setRefresh(value => value + 1)}><RefreshCw size={16} /></button>
      {active && <button className="ghost" disabled={busy} onClick={() => void command(undefined, true)}><X size={16} />Annuler</button>}
    </div>
    {scan && <div className="stock-progress" aria-live="polite"><strong>{states[scan.status] ?? scan.status}</strong>
      <span>{scan.processedFiles}/{scan.totalFiles} fichiers traités · {scan.inspectedFiles} analysés · {scan.failedFiles} échecs</span>
      {active && <progress aria-label="Progression de l'analyse" value={scan.processedFiles} max={Math.max(1, scan.totalFiles)} />}
      {scan.currentFile && <span>{scan.currentFile}</span>}
      {scan.error && <span className="stock-warning">{scan.error}</span>}
      <span className="muted">Demande : {date(scan.requestedAt)}{scan.finishedAt && ` · Fin : ${date(scan.finishedAt)}`}</span>
    </div>}
    {data && !data.hasStorage && <p className="muted">Aucun dossier monté associé à ce média.</p>}
    <div className="stock-filters">
      <input aria-label="Rechercher un fichier" placeholder="Rechercher un fichier" value={query} onChange={event => { setQuery(event.target.value); setPage(1); }} />
      <select aria-label="Saison" value={season} onChange={event => { setSeason(event.target.value); setPage(1); }}><option value="">Toutes les saisons</option>{seasons.map(value => <option key={value} value={value}>Saison {value}</option>)}</select>
      <select aria-label="État des fichiers" value={state} onChange={event => { setState(event.target.value); setPage(1); }}><option value="">Tous les fichiers présents</option><option value="Ready">Analysés</option><option value="Pending">Non analysés / modifiés</option><option value="Failed">Échecs</option><option value="Missing">Absents au dernier inventaire</option></select>
      <select aria-label="Trier les fichiers" value={sort} onChange={event => { setSort(event.target.value); setPage(1); }}><option value="episode">Nom / épisode</option><option value="size">Taille décroissante</option><option value="duration">Durée décroissante</option><option value="bitrate">Débit vidéo décroissant</option></select>
    </div>
    {!files.length && <p className="muted">{data?.files.length ? "Aucun fichier correspondant." : "Aucun inventaire enregistré."}</p>}
    {files.slice((currentPage - 1) * 25, currentPage * 25).map(file => <div className="stock-file" key={file.id}>
      <details><summary>
        <span className="stock-file-name"><strong><ChevronRight size={14} />{episode(file)?.label ?? "Fichier"}</strong><span>{file.name}</span></span>
        <span className="stock-file-metrics"><span>{file.details?.height ? `${file.details.height}p · ${file.details.videoCodec ?? "?"}` : "Vidéo non analysée"}</span><span>{size(file.sizeBytes)}</span><span>{file.isPresent ? states[file.status] ?? file.status : "Absent"}</span></span>
      </summary><FileDetails file={file} /></details>
      <button className="ghost stock-icon" disabled={busy || active || !file.isPresent || !data?.hasStorage} onClick={() => void command({ fileId: file.id, force: true })} aria-label={`Réanalyser ${file.name}`} title={`Réanalyser ${file.name}`}><RefreshCw size={16} /></button>
    </div>)}
    {!!files.length && <div className="stock-pagination"><span>{files.length} fichier(s) · Page {currentPage}/{pageCount}</span>
      <button className="ghost stock-icon" disabled={currentPage <= 1} onClick={() => setPage(currentPage - 1)} title="Page précédente" aria-label="Page précédente"><ChevronLeft size={18} /></button>
      <button className="ghost stock-icon" disabled={currentPage >= pageCount} onClick={() => setPage(currentPage + 1)} title="Page suivante" aria-label="Page suivante"><ChevronRight size={18} /></button>
    </div>}
  </section>;
}
import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Activity, Ban, Bell, Check, ChevronLeft, ChevronRight, Play, RefreshCw, Save, Search, Square } from "lucide-react";
import { api } from "../api";
import type { WatchStatus, WatchProposalPage, WatchScanPage } from "../types";

function duration(seconds: number) {
  const total = Math.max(0, Math.floor(seconds));
  return `${Math.floor(total / 60)} min ${total % 60} s`;
}

const scanStates: Record<string, string> = {
  queued: "En attente", running: "En cours", paused: "Suspendu", searching: "Préparation",
  "rate-limit": "Créneau C411", request: "Réponse C411", processing: "Traitement",
  completed: "Terminé", failed: "Erreur", skipped: "Ignoré", cancelled: "Annulé",
};

export default function Watch() {
  const [status, setStatus] = useState<WatchStatus | null>(null);
  const [enabled, setEnabled] = useState(false);
  const [minutes, setMinutes] = useState(60);
  const [decision, setDecision] = useState("Pending");
  const [page, setPage] = useState(1);
  const [proposals, setProposals] = useState<WatchProposalPage>({ total: 0, items: [] });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [activity, setActivity] = useState<WatchScanPage>({ scan: null });
  const [scanPage, setScanPage] = useState(1);
  const [scanFilter, setScanFilter] = useState("all");
  const [liveError, setLiveError] = useState<string | null>(null);
  const [lastSync, setLastSync] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    api.getWatchStatus().then(value => {
      if (!active) return;
      setStatus(value); setEnabled(value.enabled); setMinutes(value.intervalMinutes);
    }).catch(error => { if (active) setError((error as Error).message); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    async function poll() {
      try {
        const [nextStatus, nextActivity, nextProposals] = await Promise.all([
          api.getWatchStatus(controller.signal), api.getWatchScan(scanPage, scanFilter, controller.signal),
          api.getWatchProposals(decision, page, controller.signal),
        ]);
        if (controller.signal.aborted) return;
        setStatus(nextStatus); setActivity(nextActivity); setProposals(nextProposals);
        setLastSync(new Date().toLocaleTimeString("fr-FR")); setLiveError(null);
      } catch (error) {
        if (!controller.signal.aborted) setLiveError((error as Error).message);
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(poll, 2000);
      }
    }
    poll();
    return () => { controller.abort(); if (timer) clearTimeout(timer); };
  }, [scanPage, scanFilter, decision, page]);

  async function refresh() {
    const [nextStatus, nextProposals, nextActivity] = await Promise.all([api.getWatchStatus(), api.getWatchProposals(decision, page), api.getWatchScan(scanPage, scanFilter)]);
    setStatus(nextStatus); setProposals(nextProposals); setActivity(nextActivity);
  }

  async function run(action: () => Promise<void>) {
    setBusy(true); setError(null); setMessage(null);
    try { await action(); await refresh(); }
    catch (error) { setError((error as Error).message); }
    finally { setBusy(false); }
  }

  const scan = activity.scan;
  const scanning = !!scan && scan.state !== "completed" && scan.state !== "cancelled";
  const processed = scan ? scan.summary.completed + scan.summary.failed + scan.summary.skipped + (scan.summary.cancelled ?? 0) : 0;

  return <section className="watch-page">
    <div className="system-status-heading">
      <h2>Veille</h2>
      <button disabled={busy || !status?.enabled || scanning} title={scanning ? "Un scan est déjà en cours" : "Lancer un scan des médias surveillés"}
        onClick={() => run(async () => { await api.startWatchScan(); setScanPage(1); setScanFilter("all"); setMessage("Scan demandé."); })}><Play size={16} /> Lancer un scan</button>
      <button title="Actualiser la veille" aria-label="Actualiser la veille" disabled={busy} onClick={() => run(async () => {})}><RefreshCw size={16} /></button>
      {scanning && scan.manual && <button disabled={busy} onClick={() => run(async () => {
        if (!window.confirm("Annuler ce scan manuel ? Les résultats déjà enregistrés seront conservés.")) return;
        await api.cancelWatchScan(scan.id); setMessage("Scan manuel annulé.");
      })}><Square size={16} /> Annuler le scan</button>}
    </div>
    {error && <p className="error" role="alert">{error}</p>}
    {message && <p role="status">{message}</p>}
    <form className="watch-settings" onSubmit={event => { event.preventDefault(); run(() => api.configureWatch({ enabled, intervalMinutes: minutes, maxParallelSearches: 1 })); }}>
      <label className="watch-toggle"><input type="checkbox" checked={enabled} onChange={event => setEnabled(event.target.checked)} disabled={busy || !status} /> Veille active</label>
      <label>Intervalle (minutes)<input type="number" min={60} max={10080} required value={minutes} onChange={event => setMinutes(Number(event.target.value))} disabled={busy || !status} /></label>
      <button type="submit" disabled={busy || !status}><Save size={16} /> Enregistrer</button>
    </form>
    {status && <div className="watch-summary">
      <span>Référence : {status.initializedMedia} / {status.totalMedia} médias</span>
      <span>En attente : {status.pending}</span>
      <span>Erreurs de recherche : {status.errors}</span>
      <span>Dernière recherche : {status.lastCheckedAt ? new Date(status.lastCheckedAt).toLocaleString("fr-FR") : "Aucune"}</span>
      <span>Telegram : {status.telegramConfigured ? "Configuré" : "Non configuré"}</span>
      <button disabled={busy || !status.telegramConfigured} onClick={() => run(async () => { await api.testWatchTelegram(); setMessage("Notification de test envoyée."); })}><Bell size={16} /> Tester Telegram</button>
    </div>}
    <section className="watch-scan" aria-label="Progression du scan">
      <div className="watch-scan-heading"><h3><Activity size={18} /> {scan ? `Scan ${scan.manual ? "manuel" : "automatique"}` : "Activité des scans"}</h3>
        <span className="muted">{lastSync ? `Actualisé à ${lastSync}` : "Connexion..."}</span></div>
      {liveError && <p className="error" role="alert">Suivi interrompu : {liveError}</p>}
      {!scan && <p className="muted">Aucun scan enregistré.</p>}
      {scan && <>
        <div className="watch-scan-metrics">
          <strong role="status">{scanning && !scan.globalEnabled ? "Veille globale en pause" : scanning && !scan.workerOnline ? "En attente du worker" : scanStates[scan.state] ?? scan.state}</strong>
          <span>{processed} / {scan.summary.total} médias traités</span><span>{scan.summary.active} en cours</span>
          <span>{scan.summary.queued} en attente</span><span>{scan.summary.completed} réussis</span><span>{scan.summary.failed} erreurs</span><span>{scan.summary.skipped} ignorés</span>
          <span>{scan.summary.newProposals} nouvelles propositions</span><span>Durée : {duration(scan.elapsedSeconds)}</span>
          {!!scan.summary.cancelled && <span>{scan.summary.cancelled} annulés</span>}
        </div>
        <progress aria-label="Médias traités" max={Math.max(1, scan.summary.total)} value={processed} />
        <div className="watch-scan-context">
          <span>{scan.detail}</span>
          {scan.retryAt && <span>Reprise au plus tôt : {new Date(scan.retryAt).toLocaleString("fr-FR")}</span>}
          {scanning && <span>{scan.maxParallelSearches === 1 ? "Une recherche à la fois" : `${scan.maxParallelSearches} recherches parallèles`} · Espacement C411 : {scan.requestSpacingSeconds} s · Minimum restant hors latence : {duration(scan.minimumRemainingSeconds)}</span>}
          {scanning && !scan.workerOnline && <span className="error">Worker absent ou heartbeat vieux de plus de 60 s. {scan.workerLastSeen && `Dernier signal : ${new Date(scan.workerLastSeen).toLocaleString("fr-FR")}`}</span>}
        </div>
        {scanning && <section className="watch-active-searches" aria-label="Recherches en cours" aria-busy={!!liveError}>
          <h3><Search size={18} /> Recherches en cours <span className="muted">{activity.activeItems?.length ?? 0}</span></h3>
          {(activity.activeItems ?? []).map(item => <div className="watch-scan-item" key={item.id}>
            <div><Link to={`/media/${item.mediaItemId}`}>{item.title}</Link><p>{item.detail}</p></div>
            <div><strong>{scan.workerOnline ? scanStates[item.state] ?? item.state : "Dernier état connu"}</strong>
              {item.startedAt && <span>Durée : {duration((Date.now() - Date.parse(item.startedAt)) / 1000)}</span>}
              <span>Étape : {duration((Date.now() - Date.parse(item.updatedAt)) / 1000)}</span>
            </div>
          </div>)}
          {!activity.activeItems?.length && <p className="muted">{!scan.workerOnline ? "En attente du worker." :
            !scan.globalEnabled ? "Veille globale en pause." : scan.state === "paused" ? "Recherches suspendues." :
            scan.retryAt ? "En attente du prochain lot." : "En attente du démarrage des recherches."}</p>}
        </section>}
        <div className="watch-filter"><label>Médias du scan <select value={scanFilter} onChange={event => { setScanFilter(event.target.value); setScanPage(1); }}>
          <option value="all">Tous</option><option value="active">En cours</option><option value="queued">En attente</option>
          <option value="completed">Terminés</option><option value="failed">Erreurs</option><option value="skipped">Ignorés</option>
          <option value="cancelled">Annulés</option>
        </select></label><span>{activity.filteredTotal ?? 0} médias</span></div>
        <div className="watch-scan-items">
          {(activity.items ?? []).map(item => <div className={`watch-scan-item watch-scan-${item.state}`} key={item.id}>
            <div><Link to={`/media/${item.mediaItemId}`}>{item.title}</Link><p>{item.detail}</p></div>
            <div><strong>{scanStates[item.state] ?? item.state}</strong>
              {item.startedAt && <span>{duration(((item.finishedAt ? Date.parse(item.finishedAt) : Date.now()) - Date.parse(item.startedAt)) / 1000)}</span>}
              {item.startedAt && !item.finishedAt && <span>Étape : {duration((Date.now() - Date.parse(item.updatedAt)) / 1000)}</span>}
              {item.state === "completed" && <span>{item.found} releases reçues · {item.observations} correspondances · {item.filtered} rejets du profil · +{item.newProposals} propositions</span>}
              <span className="muted">{new Date(item.updatedAt).toLocaleTimeString("fr-FR")}</span></div>
          </div>)}
          {activity.items?.length === 0 && <p className="muted">Aucun média dans ce filtre.</p>}
        </div>
        <div className="watch-pagination">
          <button aria-label="Médias précédents" title="Médias précédents" disabled={scanPage === 1} onClick={() => setScanPage(scanPage - 1)}><ChevronLeft size={16} /></button>
          <span>Page {scanPage} / {Math.max(1, Math.ceil((activity.filteredTotal ?? 0) / 25))}</span>
          <button aria-label="Médias suivants" title="Médias suivants" disabled={scanPage * 25 >= (activity.filteredTotal ?? 0)} onClick={() => setScanPage(scanPage + 1)}><ChevronRight size={16} /></button>
        </div>
      </>}
    </section>
    <div className="watch-filter">
      <label>Propositions <select value={decision} disabled={busy} onChange={event => { setDecision(event.target.value); setPage(1); }}>
        <option value="Pending">En attente</option><option value="Accepted">Acceptées</option><option value="Blacklisted">Blacklistées</option>
        <option value="Expired">Expirées</option><option value="Baseline">Référence initiale</option><option value="Filtered">Filtrées</option>
      </select></label>
      <span>{proposals.total} résultats</span>
    </div>
    <div className="watch-proposals">
      {proposals.items.map(proposal => <article className="watch-proposal" key={proposal.id}>
        <div><Link to={`/media/${proposal.mediaItemId}`}>{proposal.mediaTitle}</Link><p>{proposal.title}</p>
          <span className="muted">{proposal.coverage} · {(proposal.sizeBytes / 1073741824).toFixed(2)} Gio</span></div>
        <div className="muted">{new Date(proposal.firstSeenAt).toLocaleString("fr-FR")}
          {decision === "Pending" && <p>Expire le {new Date(proposal.expiresAt).toLocaleString("fr-FR")}</p>}</div>
        {decision === "Pending" && <div className="watch-actions">
          <button disabled={busy} onClick={() => run(async () => { if (!window.confirm(`Ajouter « ${proposal.title} » à la pipeline ?`)) return; const result = await api.decideWatch(proposal.id, true); setMessage(result.message); })}><Check size={16} /> Accepter</button>
          <button className="ghost" disabled={busy} onClick={() => run(async () => { const result = await api.decideWatch(proposal.id, false); setMessage(result.message); })}><Ban size={16} /> Blacklister</button>
        </div>}
      </article>)}
      {proposals.items.length === 0 && <p className="muted">Aucune proposition.</p>}
    </div>
    <div className="watch-pagination">
      <button aria-label="Page précédente" title="Page précédente" disabled={busy || page === 1} onClick={() => setPage(page - 1)}><ChevronLeft size={16} /></button>
      <span>Page {page} / {Math.max(1, Math.ceil(proposals.total / 50))}</span>
      <button aria-label="Page suivante" title="Page suivante" disabled={busy || page * 50 >= proposals.total} onClick={() => setPage(page + 1)}><ChevronRight size={16} /></button>
    </div>
  </section>;
}
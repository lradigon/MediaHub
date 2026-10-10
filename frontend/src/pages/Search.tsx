import { useState } from "react";
import { ArrowRight, CheckCircle2, X } from "lucide-react";
import { Link } from "react-router-dom";
import { api, TMDB_IMAGE } from "../api";
import type { MediaSummary, TmdbSearchItem } from "../types";

type AddedMedia = {
  media: MediaSummary;
  posterPath?: string | null;
};

export default function Search() {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<TmdbSearchItem[]>([]);
  const [busy, setBusy] = useState(false);
  const [addingId, setAddingId] = useState<number | null>(null);
  const [added, setAdded] = useState<AddedMedia | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!query.trim()) return;
    setBusy(true);
    setError(null);
    try {
      setResults(await api.searchTmdb(query));
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function add(item: TmdbSearchItem) {
    setAddingId(item.tmdbId);
    setError(null);
    try {
      const media = await api.addMedia(item.tmdbId, item.type);
      setAdded({ media, posterPath: item.posterPath });
    } catch (err) {
      setError(`Échec de l'ajout : ${(err as Error).message}`);
    } finally {
      setAddingId(null);
    }
  }

  return (
    <div>
      <form onSubmit={submit} className="search-bar">
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Rechercher un film ou une série…"
        />
        <button type="submit" disabled={busy}>
          {busy ? "…" : "Rechercher"}
        </button>
      </form>

      {error && <p className="error">Erreur : {error}</p>}

      <div className="grid">
        {results.map((item) => (
          <article key={`${item.type}-${item.tmdbId}`} className="card">
            {item.posterPath ? (
              <img src={`${TMDB_IMAGE}${item.posterPath}`} alt={item.title} />
            ) : (
              <div className="poster-placeholder" />
            )}
            <div className="card-body">
              <strong>{item.title}</strong>
              <span className="muted">
                {item.type} {item.year ? `· ${item.year}` : ""}
              </span>
              <button onClick={() => add(item)} disabled={addingId === item.tmdbId}>
                {addingId === item.tmdbId ? "Ajout…" : "Ajouter"}
              </button>
            </div>
          </article>
        ))}
      </div>

      {added && (
        <div className="add-media-dialog-backdrop" role="presentation">
          <section className="add-media-dialog" role="dialog" aria-modal="true" aria-labelledby="add-media-title">
            <button
              className="add-media-dialog-close"
              type="button"
              onClick={() => setAdded(null)}
              aria-label="Fermer la confirmation"
              title="Fermer"
            >
              <X size={18} aria-hidden="true" />
            </button>
            <div className="add-media-dialog-mark" aria-hidden="true">
              <CheckCircle2 size={28} />
            </div>
            <div className="add-media-dialog-content">
              <p className="add-media-dialog-kicker">Ajouté à la bibliothèque</p>
              <h2 id="add-media-title">{added.media.title}</h2>
              <p className="muted">
                {added.media.type} {added.media.year ? `· ${added.media.year}` : ""} · Hors stock
              </p>
            </div>
            {added.posterPath ? (
              <img className="add-media-dialog-poster" src={`${TMDB_IMAGE}${added.posterPath}`} alt="" />
            ) : (
              <div className="add-media-dialog-poster poster-placeholder" aria-hidden="true" />
            )}
            <div className="add-media-dialog-actions">
              <button className="ghost" type="button" onClick={() => setAdded(null)}>Fermer</button>
              <Link className="add-media-dialog-link" to={`/media/${added.media.id}`}>
                Voir la fiche <ArrowRight size={16} aria-hidden="true" />
              </Link>
            </div>
          </section>
        </div>
      )}
    </div>
  );
}

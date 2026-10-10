import { useEffect, useState } from "react";
import { api, TMDB_IMAGE } from "../api";
import type { MediaRequest } from "../types";

export default function Requests() {
  const [requests, setRequests] = useState<MediaRequest[]>([]);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    try {
      setRequests(await api.getRequests());
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  useEffect(() => {
    load();
  }, []);

  async function decide(id: string, approve: boolean) {
    try {
      await (approve ? api.approveRequest(id) : api.rejectRequest(id));
      await load();
    } catch (e) {
      alert(`Échec : ${(e as Error).message}`);
    }
  }

  if (error) return <p className="error">Erreur : {error}</p>;
  if (requests.length === 0) return <p>Aucune requête.</p>;

  return (
    <table className="table">
      <thead>
        <tr>
          <th></th>
          <th>Titre</th>
          <th>Type</th>
          <th>Statut</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        {requests.map((r) => (
          <tr key={r.id}>
            <td>
              {r.posterPath && <img className="thumb" src={`${TMDB_IMAGE}${r.posterPath}`} alt="" />}
            </td>
            <td>
              {r.title} {r.year ? `(${r.year})` : ""}
            </td>
            <td>{r.type}</td>
            <td>
              <span className={`badge status-${r.status.toLowerCase()}`}>{r.status}</span>
            </td>
            <td>
              {r.status === "Pending" && (
                <>
                  <button onClick={() => decide(r.id, true)}>Approuver</button>
                  <button className="ghost" onClick={() => decide(r.id, false)}>
                    Rejeter
                  </button>
                </>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

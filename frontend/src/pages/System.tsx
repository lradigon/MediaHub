import { useEffect, useState } from "react";
import { RefreshCw } from "lucide-react";
import { api } from "../api";
import type { ServiceStatus } from "../types";

export default function System() {
  const [services, setServices] = useState<ServiceStatus[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [checkedAt, setCheckedAt] = useState<Date | null>(null);

  async function refresh() {
    setLoading(true);
    setError(null);
    try {
      setServices(await api.getSystemStatus());
      setCheckedAt(new Date());
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { refresh(); }, []);

  return (
    <section className="system-status">
      <div className="system-status-heading">
        <h2>Système</h2>
        <button onClick={refresh} disabled={loading} title="Actualiser l'état des services">
          <RefreshCw size={16} aria-hidden="true" /> {loading ? "Vérification…" : "Actualiser"}
        </button>
      </div>
      {checkedAt && <p className="muted">Dernière vérification : {checkedAt.toLocaleTimeString("fr-FR")}</p>}
      {error && <p className="error">{error}</p>}
      <div className="system-status-table">
        <table className="table">
          <thead><tr><th>Service</th><th>État</th><th>Réponse</th></tr></thead>
          <tbody>
            {services.map(service => (
              <tr key={service.name}>
                <td>{service.name}</td>
                <td><span className={`badge status-${service.state === "up" ? "available" : service.state === "down" ? "failed" : "wanted"}`}>{service.detail}</span></td>
                <td>{service.state === "unconfigured" ? "-" : `${service.latencyMilliseconds} ms`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
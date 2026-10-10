import { useEffect, useState } from "react";
import { api } from "../api";
import type { C411AccountStats, ReleaseProfile } from "../types";

function formatSize(bytes: number): string {
  if (!bytes) return "0 o";
  const gb = bytes / 1024 ** 3;
  if (gb >= 1) return `${gb.toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} Go`;
  return `${(bytes / 1024 ** 2).toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} Mo`;
}

/** Champ liste édité en texte séparé par des virgules. */
function ListField({
  label,
  help,
  value,
  onChange,
}: {
  label: string;
  help?: string;
  value: string[];
  onChange: (v: string[]) => void;
}) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      {help && <span className="field-help">{help}</span>}
      <input
        value={value.join(", ")}
        onChange={(e) =>
          onChange(
            e.target.value
              .split(",")
              .map((s) => s.trim())
              .filter(Boolean),
          )
        }
      />
      <div className="chips">
        {value.map((v) => (
          <span key={v} className="chip">
            {v}
          </span>
        ))}
      </div>
    </label>
  );
}

export default function Settings() {
  const [profile, setProfile] = useState<ReleaseProfile | null>(null);
  const [c411Stats, setC411Stats] = useState<C411AccountStats | null>(null);
  const [c411Error, setC411Error] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    api.getProfile().then(setProfile).catch((e) => setError((e as Error).message));
    api.getC411AccountStats().then(setC411Stats).catch((e) => setC411Error((e as Error).message));
  }, []);

  if (error) return <p className="error">Erreur : {error}</p>;
  if (!profile) return <p>Chargement…</p>;

  function set<K extends keyof ReleaseProfile>(key: K, val: ReleaseProfile[K]) {
    setProfile((p) => (p ? { ...p, [key]: val } : p));
    setSaved(false);
  }

  async function save() {
    if (!profile) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await api.updateProfile(profile);
      setProfile(updated);
      setSaved(true);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="settings">
      <h2>Profil de qualité</h2>
      <p className="muted">
        Ces règles décident quelles releases sont acceptées automatiquement (et le classement).
      </p>

      <section className="c411-account">
        <div className="c411-account-heading">
          <div>
            <span className="c411-account-kicker">C411</span>
            <h3>Mon ratio</h3>
          </div>
          {c411Stats && <strong className="c411-ratio">{c411Stats.ratio.toLocaleString("fr-FR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</strong>}
        </div>
        {c411Stats ? (
          <>
            <p className="muted">Compte {c411Stats.username}</p>
            <div className="c411-account-metrics">
              <div>
                <span>Uploadé</span>
                <strong>{formatSize(c411Stats.uploadedBytes)}</strong>
              </div>
              <div>
                <span>Téléchargé</span>
                <strong>{formatSize(c411Stats.downloadedBytes)}</strong>
              </div>
            </div>
          </>
        ) : (
          <p className="muted">{c411Error ?? "Chargement des statistiques C411…"}</p>
        )}
      </section>

      <label className="field">
        <span className="field-label">Nom</span>
        <input value={profile.name} onChange={(e) => set("name", e.target.value)} />
      </label>

      <div className="field-row">
        <label className="field inline">
          <input
            type="checkbox"
            checked={profile.requireMulti}
            onChange={(e) => set("requireMulti", e.target.checked)}
          />
          <span>Exiger une piste MULTi</span>
        </label>

        <label className="field">
          <span className="field-label">Seeders minimum</span>
          <input
            type="number"
            min={0}
            value={profile.minSeeders}
            onChange={(e) => set("minSeeders", Number(e.target.value))}
          />
        </label>
      </div>

      <ListField
        label="Résolutions autorisées"
        help="Vide = toutes. Ex : 1080p, 720p"
        value={profile.allowedResolutions}
        onChange={(v) => set("allowedResolutions", v)}
      />
      <ListField
        label="Codecs autorisés"
        help="Ex : x264, h264, avc"
        value={profile.allowedVideoCodecs}
        onChange={(v) => set("allowedVideoCodecs", v)}
      />
      <ListField
        label="Codecs rejetés"
        help="Ex : x265, h265, hevc"
        value={profile.rejectedVideoCodecs}
        onChange={(v) => set("rejectedVideoCodecs", v)}
      />
      <ListField
        label="Sources autorisées"
        help="Ex : WEB-DL, WEB, WEBRip, BluRay, Remux"
        value={profile.allowedSources}
        onChange={(v) => set("allowedSources", v)}
      />
      <ListField
        label="Termes rejetés"
        help="Ex : CAM, TS, TELESYNC, HDTS"
        value={profile.rejectedTerms}
        onChange={(v) => set("rejectedTerms", v)}
      />

      <div className="settings-actions">
        <button onClick={save} disabled={saving}>
          {saving ? "Enregistrement…" : "Enregistrer"}
        </button>
        {saved && <span className="saved-note">✓ Enregistré</span>}
      </div>
    </div>
  );
}

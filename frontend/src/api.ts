import type {
  JobDto,
  JobPage,
  C411AccountStats,
  EpisodeNumberingRule,
  TrackSelection,
  MediaDetail,
  MediaRequest,
  MediaSummary,
  MediaStatus,
  MediaType,
  ReleaseDto,
  ReleaseSearchResponse,
  ReleaseProfile,
  RequestStatus,
  StockMediaFile,
  ServiceStatus,
  TmdbSearchItem,
} from "./types";

const BASE = import.meta.env.VITE_API_URL ?? "";
export const TMDB_IMAGE = "https://image.tmdb.org/t/p/w200";
export const TMDB_BACKDROP = "https://image.tmdb.org/t/p/w780";

async function http<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { "Content-Type": "application/json" },
    ...init,
  });
  const text = await res.text();
  if (!res.ok) {
    throw new Error(text || `${res.status} ${res.statusText}`);
  }
  // Gère les réponses sans corps (202 Accepted, 204 No Content…).
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  getSystemStatus: () => http<ServiceStatus[]>("/api/system/status"),
  searchTmdb: (query: string) =>
    http<TmdbSearchItem[]>(`/api/search?query=${encodeURIComponent(query)}`),

  searchReleases: (query: string) =>
    http<ReleaseDto[]>(`/api/search/releases?query=${encodeURIComponent(query)}`),

  getLibrary: () => http<MediaSummary[]>(`/api/media`),

  scanStorage: () => http<void>(`/api/media/scan`, { method: "POST" }),

  getMedia: (id: string) => http<MediaDetail>(`/api/media/${id}`),

  getMediaJobs: (id: string) => http<JobDto[]>(`/api/media/${id}/jobs`),

  getMediaStock: (id: string) => http<StockMediaFile[]>(`/api/media/${id}/stock`),

  updateMediaAssociation: (id: string, tmdbId: number, type: MediaType) =>
    http<MediaDetail>(`/api/media/${id}/association`, {
      method: "PUT",
      body: JSON.stringify({ tmdbId, type }),
    }),

  updateMediaStatus: (id: string, status: MediaStatus) =>
    http<MediaDetail>(`/api/media/${id}/status`, {
      method: "PUT",
      body: JSON.stringify({ status }),
    }),

  getMediaReleases: (id: string, forceRefresh = false, coverage?: string | null) => {
    const parameters = new URLSearchParams();
    if (forceRefresh) parameters.set("forceRefresh", "true");
    if (coverage) parameters.set("coverage", coverage);
    const query = parameters.toString();
    return http<ReleaseSearchResponse>(`/api/media/${id}/releases${query ? `?${query}` : ""}`);
  },

  getEpisodeNumberingRules: (id: string) =>
    http<EpisodeNumberingRule[]>(`/api/media/${id}/episode-numbering`),

  updateEpisodeNumberingRules: (id: string, rules: EpisodeNumberingRule[]) =>
    http<EpisodeNumberingRule[]>(`/api/media/${id}/episode-numbering`, {
      method: "PUT",
      body: JSON.stringify({ rules }),
    }),

  syncEpisodes: (id: string) =>
    http<{ seasons: number; total: number; available: number; missing: number; unaired: number }>(
      `/api/media/${id}/episodes/sync`,
      { method: "POST" },
    ),

  downloadRelease: (id: string, downloadUrl: string, title?: string | null, coverage?: string | null) =>
    http<void>(`/api/media/${id}/download`, {
      method: "POST",
      body: JSON.stringify({ downloadUrl, title, coverage }),
    }),

  cancelMediaProcessing: (id: string) => http<void>(`/api/media/${id}/cancel`, { method: "POST" }),

  addMedia: (tmdbId: number, type: MediaType) =>
    http<MediaSummary>(`/api/media`, {
      method: "POST",
      body: JSON.stringify({ tmdbId, type }),
    }),

  deleteMedia: (id: string) => http<void>(`/api/media/${id}`, { method: "DELETE" }),

  getRequests: (status?: RequestStatus) =>
    http<MediaRequest[]>(`/api/requests${status ? `?status=${status}` : ""}`),

  createRequest: (tmdbId: number, type: MediaType) =>
    http<MediaRequest>(`/api/requests`, {
      method: "POST",
      body: JSON.stringify({ tmdbId, type }),
    }),

  approveRequest: (id: string) =>
    http<void>(`/api/requests/${id}/approve`, { method: "POST" }),

  rejectRequest: (id: string) =>
    http<void>(`/api/requests/${id}/reject`, { method: "POST" }),

  getJobs: () => http<JobDto[]>(`/api/jobs`),

  getJobsPage: (page: number, pageSize: number, filters?: { search?: string; kind?: string; status?: string }) => {
    const parameters = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    if (filters?.search) parameters.set("search", filters.search);
    if (filters?.kind) parameters.set("kind", filters.kind);
    if (filters?.status) parameters.set("status", filters.status);
    return http<JobPage>(`/api/jobs/page?${parameters}`);
  },

  getC411AccountStats: () => http<C411AccountStats>(`/api/c411/account`),

  cancelJob: (id: string) => http<void>(`/api/jobs/${id}/cancel`, { method: "POST" }),

  retryJob: (id: string) => http<void>(`/api/jobs/${id}/retry`, { method: "POST" }),

  getTrackSelection: (id: string) => http<TrackSelection>(`/api/jobs/${id}/track-selection`),

  confirmTrackSelection: (id: string, audioTrackIndexes: number[], subtitleTrackIndexes: number[]) =>
    http<void>(`/api/jobs/${id}/track-selection`, {
      method: "POST",
      body: JSON.stringify({ audioTrackIndexes, subtitleTrackIndexes }),
    }),

  confirmMatchingTrackSelections: (id: string, audioTrackIndexes: number[], subtitleTrackIndexes: number[]) =>
    http<{ appliedCount: number }>(`/api/jobs/${id}/track-selection/group`, {
      method: "POST",
      body: JSON.stringify({ audioTrackIndexes, subtitleTrackIndexes }),
    }),

  getProfile: () => http<ReleaseProfile>(`/api/profile`),

  updateProfile: (profile: ReleaseProfile) =>
    http<ReleaseProfile>(`/api/profile`, { method: "PUT", body: JSON.stringify(profile) }),
};

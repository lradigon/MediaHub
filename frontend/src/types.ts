export interface MediaWatchStatus {
  enabled: boolean;
  globalEnabled: boolean;
  state: "active" | "excluded" | "disabled" | "initializing" | "error";
  intervalMinutes: number;
  lastCheckedAt: string | null;
  nextCheckAt: string | null;
  lastError: string | null;
}

export interface WatchStatus {
  enabled: boolean;
  intervalMinutes: number;
  maxParallelSearches: number;
  telegramConfigured: boolean;
  totalMedia: number;
  initializedMedia: number;
  lastCheckedAt: string | null;
  errors: number;
  pending: number;
}

export interface WatchScanPage {
  scan: {
    id: string;
    state: string;
    manual: boolean;
    createdAt: string;
    startedAt: string | null;
    finishedAt: string | null;
    retryAt: string | null;
    detail: string | null;
    elapsedSeconds: number;
    minimumRemainingSeconds: number;
    requestSpacingSeconds: number;
    maxParallelSearches: number;
    globalEnabled: boolean;
    workerOnline: boolean;
    workerLastSeen: string | null;
    summary: { total: number; completed: number; failed: number; skipped: number; cancelled: number; queued: number; active: number; newProposals: number; lastActivityAt: string };
  } | null;
  items?: {
    id: string;
    mediaItemId: string;
    title: string;
    state: string;
    detail: string;
    startedAt: string | null;
    finishedAt: string | null;
    updatedAt: string;
    observations: number;
    found: number;
    filtered: number;
    newProposals: number;
  }[];
  filteredTotal?: number;
  activeItems?: NonNullable<WatchScanPage["items"]>;
}

export interface WatchProposalPage {
  total: number;
  items: {
    id: string;
    mediaItemId: string;
    mediaTitle: string;
    title: string;
    sizeBytes: number;
    coverage: string;
    decision: string;
    firstSeenAt: string;
    expiresAt: string;
    notifiedAt: string | null;
    jobId: string | null;
  }[];
}

export type MediaType = "Movie" | "Series" | "Anime";
export type MediaStatus = "Wanted" | "Processing" | "Available" | "Ignored";
export type RequestStatus = "Pending" | "Approved" | "Rejected";
export type JobKind = "Search" | "Download" | "Encode" | "Upload" | "Notify" | "Scan" | "MetadataRefresh" | "TrackSelection";
export type JobStatus = "Queued" | "Running" | "Completed" | "Failed" | "Cancelled" | "WaitingForSelection";

export interface ServiceStatus {
  name: string;
  state: "up" | "down" | "unconfigured";
  detail: string;
  latencyMilliseconds: number;
}

export interface TmdbSearchItem {
  tmdbId: number;
  type: MediaType;
  title: string;
  originalTitle?: string | null;
  year?: number | null;
  overview?: string | null;
  posterPath?: string | null;
  backdropPath?: string | null;
}

export interface MediaSummary {
  watchActive: boolean;
  id: string;
  tmdbId?: number | null;
  type: MediaType;
  title: string;
  year?: number | null;
  releaseDate?: string | null;
  posterPath?: string | null;
  backdropPath?: string | null;
  status: MediaStatus;
  inStock: boolean;
  pipelineStage?: JobKind | null;
  latestAiredEpisodeDate?: string | null;
  missingAiredEpisodeCount: number;
  storageName?: string | null;
}

export interface MediaRequest {
  id: string;
  tmdbId: number;
  type: MediaType;
  title: string;
  year?: number | null;
  posterPath?: string | null;
  status: RequestStatus;
  createdAt: string;
}

export interface ReleaseDto {
  title: string;
  downloadUrl?: string | null;
  sizeBytes: number;
  seeders: number;
  resolution?: string | null;
  videoCodec?: string | null;
  source?: string | null;
  isMulti: boolean;
  accepted: boolean;
  score: number;
  rejectionReasons: string[];
  publishDate?: string | null;
  coverage: string;
  audioCodec?: string | null;
  audioChannels?: string | null;
  dynamicRange?: string | null;
  bitDepth?: number | null;
  grabs: number;
  freeleech: boolean;
}

export interface ReleaseSearchResponse {
  query: string;
  releases: ReleaseDto[];
  checkedAt: string | null;
}

export interface JobDto {
  id: string;
  kind: JobKind;
  status: JobStatus;
  progress: number;
  mediaItemId?: string | null;
  mediaTitle?: string | null;
  seeders: number;
  sizeBytes: number;
  downloadSpeed: number;
  encodingFps?: number | null;
  error?: string | null;
  createdAt: string;
  estimatedRemainingSeconds?: number | null;
  cloudPath?: string | null;
  seasonNumber?: number | null;
  episodeNumber?: number | null;
  episodeTitle?: string | null;
}

export interface JobPage {
  page: number;
  pageSize: number;
  totalCount: number;
  jobs: JobDto[];
}

export interface C411AccountStats {
  username: string;
  uploadedBytes: number;
  downloadedBytes: number;
  ratio: number;
}

export interface JobAudioTrack {
  index: number;
  language?: string | null;
  codec?: string | null;
  channels?: string | null;
  sampleRate?: number | null;
  name?: string | null;
  isAudioDescription: boolean;
}

export interface JobSubtitleTrack {
  index: number;
  language?: string | null;
  format?: string | null;
  source?: string | null;
  isDefault: boolean;
  isForced: boolean;
  isSdh: boolean;
}

export interface TrackSelection {
  jobId: string;
  mediaTitle?: string | null;
  originalLanguage?: string | null;
  reason?: string | null;
  audioTracks: JobAudioTrack[];
  subtitleTracks: JobSubtitleTrack[];
  recommendedAudioTrackIndexes: number[];
  recommendedSubtitleTrackIndexes: number[];
  missingRequirements: string[];
}

export interface EpisodeDetail {
  id: string;
  episodeNumber: number;
  title?: string | null;
  airDate?: string | null;
  status: string;
  cloudPath?: string | null;
}

export interface SeasonDetail {
  id: string;
  seasonNumber: number;
  title?: string | null;
  episodes: EpisodeDetail[];
}

export interface MediaDetail {
  id: string;
  tmdbId?: number | null;
  type: MediaType;
  title: string;
  originalTitle?: string | null;
  overview?: string | null;
  year?: number | null;
  posterPath?: string | null;
  status: MediaStatus;
  inStock: boolean;
  pipelineStage?: JobKind | null;
  storageName?: string | null;
  seasons: SeasonDetail[];
  watch?: MediaWatchStatus | null;
}

export interface EpisodeNumberingRule {
  releaseSeasonNumber: number;
  tmdbSeasonNumber: number;
  firstTmdbEpisodeNumber: number;
}

export interface StockAudioTrack {
  language?: string | null;
  codec?: string | null;
  channels?: string | null;
  sampleRate?: number | null;
}

export interface StockSubtitleTrack {
  language?: string | null;
  format?: string | null;
  source?: string | null;
  isDefault: boolean;
  isForced: boolean;
}

export interface StockMediaFile {
  name: string;
  sizeBytes: number;
  duration?: string | null;
  width?: number | null;
  height?: number | null;
  videoCodec?: string | null;
  bitDepth?: number | null;
  videoBitRate?: number | null;
  audioTracks: StockAudioTrack[];
  subtitleTracks: StockSubtitleTrack[];
  isCloud: boolean;
}

export interface StockTechnicalDetails extends Omit<StockMediaFile, "name" | "isCloud" | "audioTracks"> {
  path: string;
  container?: string | null;
  overallBitRate?: number | null;
  videoProfile?: string | null;
  pixelFormat?: string | null;
  frameRate?: string | null;
  displayAspectRatio?: string | null;
  colorPrimaries?: string | null;
  colorTransfer?: string | null;
  colorSpace?: string | null;
  hdr?: string | null;
  externalSubtitles: string[];
  audioTracks: (StockAudioTrack & {
    index: number; name?: string | null; bitRate?: number | null; channelCount?: number | null;
    profile?: string | null; isDefault: boolean; isForced: boolean; isCommentary: boolean; isAudioDescription: boolean;
  })[];
}

export interface StockFileInspection {
  id: string;
  name: string;
  sizeBytes: number;
  modifiedAt: string;
  inspectedAt?: string | null;
  status: string;
  isPresent: boolean;
  error?: string | null;
  details?: StockTechnicalDetails | null;
}

export interface StockInspectionProgress {
  status: string;
  requestedAt: string;
  finishedAt?: string | null;
  totalFiles: number;
  processedFiles: number;
  inspectedFiles: number;
  failedFiles: number;
  currentFile?: string | null;
  error?: string | null;
}

export interface StockDetails {
  mediaId: string;
  title: string;
  posterPath?: string | null;
  hasStorage: boolean;
  inspection?: StockInspectionProgress | null;
  files: StockFileInspection[];
}

export interface ReleaseProfile {
  id: string;
  name: string;
  requireMulti: boolean;
  minSeeders: number;
  allowedResolutions: string[];
  allowedVideoCodecs: string[];
  rejectedVideoCodecs: string[];
  allowedSources: string[];
  rejectedTerms: string[];
}

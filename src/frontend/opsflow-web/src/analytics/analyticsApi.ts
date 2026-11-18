import { apiFetch } from '../auth/apiClient'

export interface RagOutcomeBreakdown {
  outcome: string
  count: number
}

export interface RagDailyTrend {
  date: string
  requests: number
  answered: number
  failed: number
}

export interface RagPipelineSummary {
  totalRequests: number
  answeredCount: number
  insufficientEvidenceCount: number
  failedCount: number
  averageDurationSeconds: number
  averageRetrievalDurationSeconds: number
  averageGenerationDurationSeconds: number | null
  averageEvidenceCount: number
  outcomeBreakdown: RagOutcomeBreakdown[]
  dailyTrend: RagDailyTrend[]
}

export interface AnalyticsSummaryResponse {
  projectCount: number
  documentCount: number
  ragPipeline: RagPipelineSummary
}

export async function fetchAnalyticsSummary(): Promise<AnalyticsSummaryResponse> {
  const response = await apiFetch({
    path: '/api/v1/analytics/summary',
    method: 'GET',
  })
  if (!response.ok) {
    throw new Error(`Failed to fetch analytics: ${response.status}`)
  }
  return (await response.json()) as AnalyticsSummaryResponse
}

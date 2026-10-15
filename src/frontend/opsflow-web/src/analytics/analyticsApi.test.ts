import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../auth/apiClient', () => ({
  apiFetch: vi.fn(),
}))

import { apiFetch } from '../auth/apiClient'
import { fetchAnalyticsSummary, type AnalyticsSummaryResponse } from './analyticsApi'

const mockApiFetch = vi.mocked(apiFetch)

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

const sampleSummary: AnalyticsSummaryResponse = {
  projectCount: 3,
  documentCount: 12,
  ragPipeline: {
    totalRequests: 100,
    answeredCount: 72,
    insufficientEvidenceCount: 16,
    failedCount: 12,
    averageDurationSeconds: 1.5,
    averageRetrievalDurationSeconds: 0.4,
    averageGenerationDurationSeconds: 0.9,
    averageEvidenceCount: 4.2,
    outcomeBreakdown: [
      { outcome: 'Answered', count: 72 },
      { outcome: 'InsufficientEvidence', count: 16 },
      { outcome: 'Failed', count: 12 },
    ],
    dailyTrend: [{ date: '2026-10-01', requests: 10, answered: 7, failed: 1 }],
  },
}

beforeEach(() => {
  mockApiFetch.mockReset()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('fetchAnalyticsSummary', () => {
  it('calls GET /api/v1/analytics/summary', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(200, sampleSummary))

    await fetchAnalyticsSummary()

    expect(mockApiFetch).toHaveBeenCalledWith({
      path: '/api/v1/analytics/summary',
      method: 'GET',
    })
  })

  it('returns parsed summary on 200', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(200, sampleSummary))

    const result = await fetchAnalyticsSummary()

    expect(result.projectCount).toBe(3)
    expect(result.documentCount).toBe(12)
    expect(result.ragPipeline.totalRequests).toBe(100)
    expect(result.ragPipeline.answeredCount).toBe(72)
    expect(result.ragPipeline.insufficientEvidenceCount).toBe(16)
    expect(result.ragPipeline.failedCount).toBe(12)
    expect(result.ragPipeline.outcomeBreakdown).toHaveLength(3)
    expect(result.ragPipeline.dailyTrend[0]!.date).toBe('2026-10-01')
  })

  it('handles null averageGenerationDurationSeconds', async () => {
    const noGenSummary: AnalyticsSummaryResponse = {
      ...sampleSummary,
      ragPipeline: { ...sampleSummary.ragPipeline, averageGenerationDurationSeconds: null },
    }
    mockApiFetch.mockResolvedValue(jsonResponse(200, noGenSummary))

    const result = await fetchAnalyticsSummary()

    expect(result.ragPipeline.averageGenerationDurationSeconds).toBeNull()
  })

  it('throws when response is not ok', async () => {
    mockApiFetch.mockResolvedValue(new Response(null, { status: 403 }))

    await expect(fetchAnalyticsSummary()).rejects.toThrow('403')
  })

  it('uses apiFetch for authenticated request infrastructure', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(200, sampleSummary))

    await fetchAnalyticsSummary()

    expect(mockApiFetch).toHaveBeenCalledTimes(1)
  })
})

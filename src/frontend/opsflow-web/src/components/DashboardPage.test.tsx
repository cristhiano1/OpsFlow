import { render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

vi.mock('../analytics/analyticsApi', () => ({
  fetchAnalyticsSummary: vi.fn(),
}))

import { fetchAnalyticsSummary } from '../analytics/analyticsApi'
import type { AnalyticsSummaryResponse } from '../analytics/analyticsApi'
import { DashboardPage } from './DashboardPage'

const mockFetch = vi.mocked(fetchAnalyticsSummary)

function makeSummary(overrides?: Partial<AnalyticsSummaryResponse>): AnalyticsSummaryResponse {
  return {
    projectCount: 3,
    documentCount: 10,
    ragPipeline: {
      totalRequests: 50,
      answeredCount: 36,
      insufficientEvidenceCount: 8,
      failedCount: 6,
      averageDurationSeconds: 1.8,
      averageRetrievalDurationSeconds: 0.5,
      averageGenerationDurationSeconds: 1.1,
      averageEvidenceCount: 4.0,
      outcomeBreakdown: [
        { outcome: 'Answered', count: 36 },
        { outcome: 'InsufficientEvidence', count: 8 },
        { outcome: 'Failed', count: 6 },
      ],
      dailyTrend: [{ date: '2026-10-01', requests: 5, answered: 3, failed: 1 }],
    },
    ...overrides,
  }
}

describe('DashboardPage', () => {
  it('renders the Dashboard heading', () => {
    mockFetch.mockReturnValue(new Promise(() => {})) // never resolves
    render(<DashboardPage />)
    expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeInTheDocument()
  })

  it('shows loading state while fetching', () => {
    mockFetch.mockReturnValue(new Promise(() => {}))
    render(<DashboardPage />)
    expect(screen.getByText(/loading analytics/i)).toBeInTheDocument()
  })

  it('shows error message when fetch fails', async () => {
    mockFetch.mockRejectedValue(new Error('network error'))
    render(<DashboardPage />)
    await waitFor(() => {
      expect(screen.getByText(/network error/i)).toBeInTheDocument()
    })
  })

  it('shows summary cards when data loads', async () => {
    mockFetch.mockResolvedValue(makeSummary())
    render(<DashboardPage />)
    await waitFor(() => {
      expect(screen.getByText('3')).toBeInTheDocument() // projectCount
      expect(screen.getByText('Projects')).toBeInTheDocument()
    })
  })

  it('shows success rate card based on answered count', async () => {
    mockFetch.mockResolvedValue(makeSummary())
    render(<DashboardPage />)
    await waitFor(() => {
      expect(screen.getByText('72%')).toBeInTheDocument() // 36/50 = 72%
      expect(screen.getByText('Success Rate')).toBeInTheDocument()
    })
  })

  it('shows empty state when there are no RAG requests', async () => {
    mockFetch.mockResolvedValue(
      makeSummary({
        ragPipeline: {
          totalRequests: 0,
          answeredCount: 0,
          insufficientEvidenceCount: 0,
          failedCount: 0,
          averageDurationSeconds: 0,
          averageRetrievalDurationSeconds: 0,
          averageGenerationDurationSeconds: null,
          averageEvidenceCount: 0,
          outcomeBreakdown: [],
          dailyTrend: [],
        },
      }),
    )
    render(<DashboardPage />)
    await waitFor(() => {
      expect(screen.getByText(/no rag pipeline activity/i)).toBeInTheDocument()
    })
  })
})

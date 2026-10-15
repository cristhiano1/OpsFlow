import { useCallback, useEffect, useRef, useState } from 'react'
import {
  fetchAnalyticsSummary,
  type AnalyticsSummaryResponse,
} from '../analytics/analyticsApi'
import './DashboardPage.css'

type PageState =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'loaded'; data: AnalyticsSummaryResponse }

export function DashboardPage() {
  const [pageState, setPageState] = useState<PageState>({ status: 'loading' })
  const loadGenerationRef = useRef(0)

  const invalidateLoads = useCallback(() => {
    ++loadGenerationRef.current
  }, [])

  const loadData = useCallback(async () => {
    const generation = ++loadGenerationRef.current
    try {
      const data = await fetchAnalyticsSummary()
      if (generation === loadGenerationRef.current) {
        setPageState({ status: 'loaded', data })
      }
    } catch (err) {
      if (generation === loadGenerationRef.current) {
        setPageState({
          status: 'error',
          message: err instanceof Error ? err.message : 'Failed to load analytics',
        })
      }
    }
  }, [])

  useEffect(() => {
    void loadData()
    return invalidateLoads
  }, [loadData, invalidateLoads])

  if (pageState.status === 'loading') {
    return (
      <div className="dashboard-page">
        <h2 className="dashboard-heading">Dashboard</h2>
        <p className="dashboard-loading">Loading analytics…</p>
      </div>
    )
  }

  if (pageState.status === 'error') {
    return (
      <div className="dashboard-page">
        <h2 className="dashboard-heading">Dashboard</h2>
        <p className="dashboard-error">{pageState.message}</p>
      </div>
    )
  }

  const { projectCount, documentCount, ragPipeline } = pageState.data
  const successRate = ragPipeline.totalRequests > 0
    ? Math.round((ragPipeline.answeredCount / ragPipeline.totalRequests) * 100)
    : 0

  return (
    <div className="dashboard-page">
      <h2 className="dashboard-heading">Dashboard</h2>

      <section className="dashboard-cards">
        <div className="dashboard-card">
          <span className="dashboard-card-value">{projectCount}</span>
          <span className="dashboard-card-label">Projects</span>
        </div>
        <div className="dashboard-card">
          <span className="dashboard-card-value">{documentCount}</span>
          <span className="dashboard-card-label">Documents</span>
        </div>
        <div className="dashboard-card">
          <span className="dashboard-card-value">{ragPipeline.totalRequests}</span>
          <span className="dashboard-card-label">RAG Queries</span>
        </div>
        <div className="dashboard-card">
          <span className="dashboard-card-value">{successRate}%</span>
          <span className="dashboard-card-label">Success Rate</span>
        </div>
      </section>

      {ragPipeline.totalRequests > 0 && (
        <>
          <section className="dashboard-section">
            <h3 className="dashboard-section-title">RAG Pipeline Performance</h3>
            <div className="dashboard-metrics">
              <div className="dashboard-metric">
                <span className="dashboard-metric-value">
                  {formatDuration(ragPipeline.averageDurationSeconds)}
                </span>
                <span className="dashboard-metric-label">Avg Total</span>
              </div>
              <div className="dashboard-metric">
                <span className="dashboard-metric-value">
                  {formatDuration(ragPipeline.averageRetrievalDurationSeconds)}
                </span>
                <span className="dashboard-metric-label">Avg Retrieval</span>
              </div>
              {ragPipeline.averageGenerationDurationSeconds !== null && (
                <div className="dashboard-metric">
                  <span className="dashboard-metric-value">
                    {formatDuration(ragPipeline.averageGenerationDurationSeconds)}
                  </span>
                  <span className="dashboard-metric-label">Avg Generation</span>
                </div>
              )}
              <div className="dashboard-metric">
                <span className="dashboard-metric-value">
                  {ragPipeline.averageEvidenceCount}
                </span>
                <span className="dashboard-metric-label">Avg Evidence Chunks</span>
              </div>
            </div>
          </section>

          <section className="dashboard-section">
            <h3 className="dashboard-section-title">Outcome Breakdown</h3>
            <div className="dashboard-bar-chart">
              {ragPipeline.outcomeBreakdown.map(item => (
                <div key={item.outcome} className="dashboard-bar-row">
                  <span className="dashboard-bar-label">{formatOutcome(item.outcome)}</span>
                  <div className="dashboard-bar-track">
                    <div
                      className={`dashboard-bar-fill dashboard-bar--${item.outcome.toLowerCase()}`}
                      style={{
                        width: `${Math.max(
                          (item.count / ragPipeline.totalRequests) * 100,
                          2,
                        )}%`,
                      }}
                    />
                  </div>
                  <span className="dashboard-bar-count">{item.count}</span>
                </div>
              ))}
            </div>
          </section>

          {ragPipeline.dailyTrend.length > 0 && (
            <section className="dashboard-section">
              <h3 className="dashboard-section-title">Daily Trend (Last 30 Days)</h3>
              <div className="dashboard-trend">
                {ragPipeline.dailyTrend.map(day => {
                  const maxRequests = Math.max(...ragPipeline.dailyTrend.map(d => d.requests))
                  const height = maxRequests > 0 ? (day.requests / maxRequests) * 100 : 0
                  return (
                    <div key={day.date} className="dashboard-trend-bar" title={`${day.date}: ${day.requests} queries`}>
                      <div
                        className="dashboard-trend-fill"
                        style={{ height: `${Math.max(height, 4)}%` }}
                      />
                      <span className="dashboard-trend-label">
                        {day.date.slice(5)}
                      </span>
                    </div>
                  )
                })}
              </div>
            </section>
          )}
        </>
      )}

      {ragPipeline.totalRequests === 0 && (
        <section className="dashboard-section">
          <p className="dashboard-empty">
            No RAG pipeline activity yet. Ask a question in a project workspace to see metrics here.
          </p>
        </section>
      )}
    </div>
  )
}

function formatDuration(seconds: number): string {
  if (seconds < 1) return `${Math.round(seconds * 1000)}ms`
  return `${seconds.toFixed(1)}s`
}

function formatOutcome(outcome: string): string {
  switch (outcome) {
    case 'Answered': return 'Answered'
    case 'InsufficientEvidence': return 'Insufficient Evidence'
    case 'ProjectNotFound': return 'Project Not Found'
    case 'Failed': return 'Failed'
    case 'Canceled': return 'Canceled'
    default: return outcome
  }
}

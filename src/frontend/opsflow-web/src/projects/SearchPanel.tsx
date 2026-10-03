import { useMemo, useRef, useState } from 'react'
import {
  searchProjectDocuments,
  RagApiError,
  type SearchDocumentChunkHitResponse,
} from './ragApi'
import type { DocumentResponse } from './documentsApi'
import './SearchPanel.css'

type SearchState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'results'; items: SearchDocumentChunkHitResponse[] }
  | { status: 'error'; message: string }

function describeSearchError(err: unknown): string {
  if (err instanceof RagApiError) {
    switch (err.status) {
      case 400:
        return err.serverMessage ?? 'Invalid search query. Please rephrase and try again.'
      case 503:
        return 'The search service is currently unavailable. Please try again later.'
      default:
        return err.serverMessage ?? 'Search failed. Please try again.'
    }
  }
  return 'Search failed. Please try again.'
}

interface SearchPanelProps {
  projectId: string
  documents: DocumentResponse[]
}

export function SearchPanel({ projectId, documents }: SearchPanelProps) {
  const [query, setQuery] = useState('')
  const [searchState, setSearchState] = useState<SearchState>({ status: 'idle' })
  const generationRef = useRef(0)

  const documentNames = useMemo(() => {
    const map = new Map<string, string>()
    for (const doc of documents) {
      map.set(doc.id, doc.originalFileName)
    }
    return map
  }, [documents])

  const loading = searchState.status === 'loading'
  const canSearch = query.trim().length > 0 && !loading

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    const trimmed = query.trim()
    if (!trimmed || loading) return

    const gen = ++generationRef.current
    setSearchState({ status: 'loading' })

    try {
      const response = await searchProjectDocuments(projectId, trimmed)
      if (gen === generationRef.current) {
        setSearchState({ status: 'results', items: response.items })
      }
    } catch (err) {
      if (gen !== generationRef.current) return
      setSearchState({ status: 'error', message: describeSearchError(err) })
    }
  }

  if (documents.length === 0) {
    return (
      <div className="search-panel">
        <h3 className="search-heading">Search</h3>
        <p className="search-empty">
          No documents have been uploaded to this project yet.
          Upload documents in the Documents tab to enable search.
        </p>
      </div>
    )
  }

  return (
    <div className="search-panel">
      <h3 className="search-heading">Search</h3>
      <p className="search-description">
        Search across this project&#39;s documents using hybrid semantic and
        keyword matching.
      </p>

      <form className="search-form" onSubmit={handleSubmit}>
        <label className="search-label" htmlFor="search-query-input">
          Query
        </label>
        <div className="search-input-row">
          <input
            id="search-query-input"
            type="text"
            className="search-input"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search project documents…"
            disabled={loading}
            maxLength={2500}
          />
          <button
            type="submit"
            className="search-submit"
            disabled={!canSearch}
          >
            {loading ? 'Searching…' : 'Search'}
          </button>
        </div>
      </form>

      <div aria-live="polite" className="search-results">
        {loading && (
          <p className="search-loading" role="status">Searching…</p>
        )}

        {searchState.status === 'error' && (
          <div className="search-error" role="alert">
            {searchState.message}
          </div>
        )}

        {searchState.status === 'results' && searchState.items.length === 0 && (
          <p className="search-no-results" role="status">
            No matching content found. Try a different query.
          </p>
        )}

        {searchState.status === 'results' && searchState.items.length > 0 && (
          <>
            <p className="search-result-count">
              {searchState.items.length}{' '}
              result{searchState.items.length !== 1 ? 's' : ''}
            </p>
            <ul className="search-result-list">
              {searchState.items.map((hit) => (
                <li key={hit.documentChunkId} className="search-result-item">
                  <p className="search-result-source">
                    {documentNames.get(hit.documentId) ?? 'Document'}
                  </p>
                  <p className="search-result-chunk">
                    Chunk {hit.chunkIndex}
                  </p>
                  <p className="search-result-text">{hit.text}</p>
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
    </div>
  )
}

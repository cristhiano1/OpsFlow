import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('./ragApi', () => ({
  searchProjectDocuments: vi.fn(),
  RagApiError: class RagApiError extends Error {
    status: number
    serverMessage: string | null
    constructor(status: number, serverMessage: string | null) {
      super(`RAG API error ${status}`)
      this.name = 'RagApiError'
      this.status = status
      this.serverMessage = serverMessage
    }
  },
}))

import { searchProjectDocuments, RagApiError } from './ragApi'
import { SearchPanel } from './SearchPanel'
import type { DocumentResponse } from './documentsApi'

const mockSearch = vi.mocked(searchProjectDocuments)

const SAMPLE_DOCS: DocumentResponse[] = [
  {
    id: 'd1',
    originalFileName: 'requirements.txt',
    contentType: 'text/plain',
    sizeBytes: 1024,
    createdAt: '2026-08-01T00:00:00Z',
  },
  {
    id: 'd2',
    originalFileName: 'design.docx',
    contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    sizeBytes: 2048,
    createdAt: '2026-08-02T00:00:00Z',
  },
]

function renderSearch(documents = SAMPLE_DOCS) {
  return render(<SearchPanel projectId="proj-1" documents={documents} />)
}

beforeEach(() => {
  mockSearch.mockReset()
})

// ── Rendering ─────────────────────────────────────────────────────────────

describe('SearchPanel rendering', () => {
  it('renders search input with label', () => {
    renderSearch()
    expect(screen.getByLabelText('Query')).toBeInTheDocument()
  })

  it('renders Search button', () => {
    renderSearch()
    expect(screen.getByRole('button', { name: 'Search' })).toBeInTheDocument()
  })

  it('renders heading', () => {
    renderSearch()
    expect(screen.getByRole('heading', { name: 'Search' })).toBeInTheDocument()
  })
})

// ── Empty query prevention ────────────────────────────────────────────────

describe('Empty query prevention', () => {
  it('disables Search button when query is empty', () => {
    renderSearch()
    expect(screen.getByRole('button', { name: 'Search' })).toBeDisabled()
  })

  it('enables Search button when query has content', async () => {
    const user = userEvent.setup()
    renderSearch()
    await user.type(screen.getByLabelText('Query'), 'authentication')
    expect(screen.getByRole('button', { name: 'Search' })).toBeEnabled()
  })
})

// ── Submission ────────────────────────────────────────────────────────────

describe('Submission', () => {
  it('calls searchProjectDocuments with correct args', async () => {
    const user = userEvent.setup()
    mockSearch.mockResolvedValue({ items: [] })
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'authentication')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    expect(mockSearch).toHaveBeenCalledWith('proj-1', 'authentication')
  })

  it('shows loading state during request', async () => {
    const user = userEvent.setup()
    mockSearch.mockReturnValue(new Promise(() => {}))
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'test')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    expect(screen.getByRole('status')).toHaveTextContent(/Searching/)
  })

  it('prevents duplicate submission while loading', async () => {
    const user = userEvent.setup()
    mockSearch.mockReturnValue(new Promise(() => {}))
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'test')
    await user.click(screen.getByRole('button', { name: 'Search' }))
    await user.click(screen.getByRole('button', { name: /Searching/ }))

    expect(mockSearch).toHaveBeenCalledTimes(1)
  })
})

// ── Results rendering ─────────────────────────────────────────────────────

describe('Results rendering', () => {
  it('renders search results with document names', async () => {
    const user = userEvent.setup()
    mockSearch.mockResolvedValue({
      items: [
        {
          documentId: 'd1',
          documentChunkId: 'dc1',
          chunkIndex: 0,
          startOffset: 0,
          endOffset: 100,
          text: 'Authentication uses JWT tokens.',
        },
      ],
    })
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'auth')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(screen.getByText('requirements.txt')).toBeInTheDocument()
      expect(screen.getByText('Authentication uses JWT tokens.')).toBeInTheDocument()
      expect(screen.getByText('1 result')).toBeInTheDocument()
    })
  })

  it('renders multiple results', async () => {
    const user = userEvent.setup()
    mockSearch.mockResolvedValue({
      items: [
        {
          documentId: 'd1',
          documentChunkId: 'dc1',
          chunkIndex: 0,
          startOffset: 0,
          endOffset: 50,
          text: 'First result.',
        },
        {
          documentId: 'd2',
          documentChunkId: 'dc2',
          chunkIndex: 1,
          startOffset: 50,
          endOffset: 100,
          text: 'Second result.',
        },
      ],
    })
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'test')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(screen.getByText('2 results')).toBeInTheDocument()
      expect(screen.getAllByRole('listitem')).toHaveLength(2)
      expect(screen.getByText('requirements.txt')).toBeInTheDocument()
      expect(screen.getByText('design.docx')).toBeInTheDocument()
    })
  })

  it('shows chunk index for each result', async () => {
    const user = userEvent.setup()
    mockSearch.mockResolvedValue({
      items: [
        {
          documentId: 'd1',
          documentChunkId: 'dc1',
          chunkIndex: 5,
          startOffset: 0,
          endOffset: 50,
          text: 'Result text.',
        },
      ],
    })
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'test')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(screen.getByText('Chunk 5')).toBeInTheDocument()
    })
  })

  it('shows "Document" fallback for unknown documentId', async () => {
    const user = userEvent.setup()
    mockSearch.mockResolvedValue({
      items: [
        {
          documentId: 'unknown-id',
          documentChunkId: 'dc1',
          chunkIndex: 0,
          startOffset: 0,
          endOffset: 10,
          text: 'Text.',
        },
      ],
    })
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'test')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(screen.getByText('Document')).toBeInTheDocument()
    })
  })
})

// ── Empty results ─────────────────────────────────────────────────────────

describe('Empty results', () => {
  it('shows empty results message when no matches found', async () => {
    const user = userEvent.setup()
    mockSearch.mockResolvedValue({ items: [] })
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'nonexistent')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(screen.getByText(/No matching content found/)).toBeInTheDocument()
    })
  })
})

// ── Error handling ────────────────────────────────────────────────────────

describe('Error handling', () => {
  it('shows error on 503 (service unavailable)', async () => {
    const user = userEvent.setup()
    mockSearch.mockRejectedValue(new RagApiError(503, null))
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'test')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(
        'The search service is currently unavailable.',
      )
    })
  })

  it('shows validation error from server', async () => {
    const user = userEvent.setup()
    mockSearch.mockRejectedValue(
      new RagApiError(400, 'Query text is required.'),
    )
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'x')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Query text is required.')
    })
  })

  it('shows generic error for unknown failures', async () => {
    const user = userEvent.setup()
    mockSearch.mockRejectedValue(new Error('Network error'))
    renderSearch()

    await user.type(screen.getByLabelText('Query'), 'test')
    await user.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Search failed.')
    })
  })
})

// ── No documents state ────────────────────────────────────────────────────

describe('No documents state', () => {
  it('shows empty state when no documents exist', () => {
    renderSearch([])
    expect(
      screen.getByText(/No documents have been uploaded/),
    ).toBeInTheDocument()
  })

  it('does not show search input when no documents exist', () => {
    renderSearch([])
    expect(screen.queryByLabelText('Query')).not.toBeInTheDocument()
  })
})

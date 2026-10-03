import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('./ragApi', () => ({
  askProjectQuestion: vi.fn(),
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

import { askProjectQuestion, RagApiError } from './ragApi'
import { AskPanel } from './AskPanel'
import type { DocumentResponse } from './documentsApi'

const mockAsk = vi.mocked(askProjectQuestion)

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

function renderAsk(documents = SAMPLE_DOCS) {
  return render(<AskPanel projectId="proj-1" documents={documents} />)
}

beforeEach(() => {
  mockAsk.mockReset()
})

// ── Rendering ─────────────────────────────────────────────────────────────

describe('AskPanel rendering', () => {
  it('renders question input with label', () => {
    renderAsk()
    expect(screen.getByLabelText('Question')).toBeInTheDocument()
  })

  it('renders Ask button', () => {
    renderAsk()
    expect(screen.getByRole('button', { name: 'Ask' })).toBeInTheDocument()
  })

  it('renders heading', () => {
    renderAsk()
    expect(screen.getByText('Ask OpsFlow')).toBeInTheDocument()
  })
})

// ── Blank question prevention ─────────────────────────────────────────────

describe('Blank question prevention', () => {
  it('disables Ask button when question is empty', () => {
    renderAsk()
    expect(screen.getByRole('button', { name: 'Ask' })).toBeDisabled()
  })

  it('disables Ask button when question is only whitespace', async () => {
    const user = userEvent.setup()
    renderAsk()
    await user.type(screen.getByLabelText('Question'), '   ')
    expect(screen.getByRole('button', { name: 'Ask' })).toBeDisabled()
  })

  it('enables Ask button when question has content', async () => {
    const user = userEvent.setup()
    renderAsk()
    await user.type(screen.getByLabelText('Question'), 'What is this?')
    expect(screen.getByRole('button', { name: 'Ask' })).toBeEnabled()
  })
})

// ── Submission ────────────────────────────────────────────────────────────

describe('Submission', () => {
  it('calls askProjectQuestion with correct project and question', async () => {
    const user = userEvent.setup()
    mockAsk.mockResolvedValue({
      status: 'answered',
      answer: 'Test answer.',
      citations: [],
    })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'What is the system?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    expect(mockAsk).toHaveBeenCalledWith('proj-1', 'What is the system?')
  })

  it('shows loading state during request', async () => {
    const user = userEvent.setup()
    mockAsk.mockReturnValue(new Promise(() => {}))
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    expect(screen.getByText(/Generating answer/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Asking/ })).toBeDisabled()
  })

  it('prevents duplicate submission while loading', async () => {
    const user = userEvent.setup()
    mockAsk.mockReturnValue(new Promise(() => {}))
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))
    await user.click(screen.getByRole('button', { name: /Asking/ }))

    expect(mockAsk).toHaveBeenCalledTimes(1)
  })
})

// ── Grounded answer ───────────────────────────────────────────────────────

describe('Grounded answer rendering', () => {
  it('renders answer text', async () => {
    const user = userEvent.setup()
    mockAsk.mockResolvedValue({
      status: 'answered',
      answer: 'The system uses JWT authentication.',
      citations: [],
    })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'How does auth work?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByText('The system uses JWT authentication.')).toBeInTheDocument()
    })
  })

  it('shows Answer heading', async () => {
    const user = userEvent.setup()
    mockAsk.mockResolvedValue({
      status: 'answered',
      answer: 'Response text.',
      citations: [],
    })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByText('Answer')).toBeInTheDocument()
    })
  })
})

// ── Citations ─────────────────────────────────────────────────────────────

describe('Citation rendering', () => {
  it('renders single citation with document name', async () => {
    const user = userEvent.setup()
    mockAsk.mockResolvedValue({
      status: 'answered',
      answer: 'Answer with source.',
      citations: [
        {
          documentId: 'd1',
          documentChunkId: 'dc1',
          chunkIndex: 2,
          startOffset: 0,
          endOffset: 50,
          text: 'Source text from requirements.',
        },
      ],
    })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByText('requirements.txt')).toBeInTheDocument()
      expect(screen.getByText('Source text from requirements.')).toBeInTheDocument()
      expect(screen.getByText('Sources (1)')).toBeInTheDocument()
      expect(screen.getByText('Chunk 2')).toBeInTheDocument()
    })
  })

  it('renders multiple citations from different documents', async () => {
    const user = userEvent.setup()
    mockAsk.mockResolvedValue({
      status: 'answered',
      answer: 'Multi-source answer.',
      citations: [
        {
          documentId: 'd1',
          documentChunkId: 'dc1',
          chunkIndex: 0,
          startOffset: 0,
          endOffset: 50,
          text: 'First source.',
        },
        {
          documentId: 'd2',
          documentChunkId: 'dc2',
          chunkIndex: 3,
          startOffset: 100,
          endOffset: 200,
          text: 'Second source.',
        },
      ],
    })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByText('Sources (2)')).toBeInTheDocument()
      expect(screen.getByText('requirements.txt')).toBeInTheDocument()
      expect(screen.getByText('design.docx')).toBeInTheDocument()
      expect(screen.getByText('First source.')).toBeInTheDocument()
      expect(screen.getByText('Second source.')).toBeInTheDocument()
    })
  })

  it('shows "Document" fallback for unknown documentId', async () => {
    const user = userEvent.setup()
    mockAsk.mockResolvedValue({
      status: 'answered',
      answer: 'Answer.',
      citations: [
        {
          documentId: 'unknown-id',
          documentChunkId: 'dc1',
          chunkIndex: 0,
          startOffset: 0,
          endOffset: 10,
          text: 'Chunk text.',
        },
      ],
    })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByText('Document')).toBeInTheDocument()
    })
  })

  it('renders citation numbers sequentially', async () => {
    const user = userEvent.setup()
    mockAsk.mockResolvedValue({
      status: 'answered',
      answer: 'Answer.',
      citations: [
        {
          documentId: 'd1',
          documentChunkId: 'dc1',
          chunkIndex: 0,
          startOffset: 0,
          endOffset: 10,
          text: 'First.',
        },
        {
          documentId: 'd2',
          documentChunkId: 'dc2',
          chunkIndex: 1,
          startOffset: 0,
          endOffset: 10,
          text: 'Second.',
        },
      ],
    })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByText('[1]')).toBeInTheDocument()
      expect(screen.getByText('[2]')).toBeInTheDocument()
    })
  })
})

// ── Insufficient evidence ─────────────────────────────────────────────────

describe('Insufficient evidence', () => {
  it('shows insufficient evidence message', async () => {
    const user = userEvent.setup()
    mockAsk.mockResolvedValue({
      status: 'insufficient_evidence',
      answer: null,
      citations: [],
    })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'What about Mars?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(
        screen.getByText(/not enough evidence/),
      ).toBeInTheDocument()
    })
  })

  it('does not show Answer heading for insufficient evidence', async () => {
    const user = userEvent.setup()
    mockAsk.mockResolvedValue({
      status: 'insufficient_evidence',
      answer: null,
      citations: [],
    })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByText(/not enough evidence/)).toBeInTheDocument()
    })
    expect(screen.queryByText('Answer')).not.toBeInTheDocument()
  })
})

// ── Error handling ────────────────────────────────────────────────────────

describe('Error handling', () => {
  it('shows error on 503 (service unavailable)', async () => {
    const user = userEvent.setup()
    mockAsk.mockRejectedValue(new RagApiError(503, null))
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(
        'The AI service is currently unavailable.',
      )
    })
  })

  it('shows error on 502 (provider contract violation)', async () => {
    const user = userEvent.setup()
    mockAsk.mockRejectedValue(new RagApiError(502, null))
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(
        'The AI service returned an unexpected response.',
      )
    })
  })

  it('shows validation error from server on 400', async () => {
    const user = userEvent.setup()
    mockAsk.mockRejectedValue(
      new RagApiError(400, 'Question must contain at least one alphanumeric character.'),
    )
    renderAsk()

    await user.type(screen.getByLabelText('Question'), '???')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(
        'Question must contain at least one alphanumeric character.',
      )
    })
  })

  it('shows generic error for unknown failures', async () => {
    const user = userEvent.setup()
    mockAsk.mockRejectedValue(new Error('Network error'))
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(
        'Something went wrong.',
      )
    })
  })
})

// ── Retry / new question ──────────────────────────────────────────────────

describe('Retry and new question', () => {
  it('allows retry after error', async () => {
    const user = userEvent.setup()
    mockAsk
      .mockRejectedValueOnce(new RagApiError(503, null))
      .mockResolvedValueOnce({
        status: 'answered',
        answer: 'Success on retry.',
        citations: [],
      })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'Test?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByText('Success on retry.')).toBeInTheDocument()
    })
  })

  it('allows asking a new question after answer', async () => {
    const user = userEvent.setup()
    mockAsk
      .mockResolvedValueOnce({
        status: 'answered',
        answer: 'First answer.',
        citations: [],
      })
      .mockResolvedValueOnce({
        status: 'answered',
        answer: 'Second answer.',
        citations: [],
      })
    renderAsk()

    await user.type(screen.getByLabelText('Question'), 'First?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByText('First answer.')).toBeInTheDocument()
    })

    await user.clear(screen.getByLabelText('Question'))
    await user.type(screen.getByLabelText('Question'), 'Second?')
    await user.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => {
      expect(screen.getByText('Second answer.')).toBeInTheDocument()
    })
  })
})

// ── No documents state ────────────────────────────────────────────────────

describe('No documents state', () => {
  it('shows empty state when no documents exist', () => {
    renderAsk([])
    expect(
      screen.getByText(/No documents have been uploaded/),
    ).toBeInTheDocument()
  })

  it('does not show question input when no documents exist', () => {
    renderAsk([])
    expect(screen.queryByLabelText('Question')).not.toBeInTheDocument()
  })
})

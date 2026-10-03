import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../auth/apiClient', () => ({
  apiFetch: vi.fn(),
}))

import { apiFetch } from '../auth/apiClient'
import {
  askProjectQuestion,
  searchProjectDocuments,
  RagApiError,
} from './ragApi'
import { ProjectNotFoundError } from './documentsApi'

const mockApiFetch = vi.mocked(apiFetch)

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function problemResponse(status: number, detail: string): Response {
  return new Response(JSON.stringify({ title: 'Error', detail }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  })
}

beforeEach(() => {
  mockApiFetch.mockReset()
})

afterEach(() => {
  vi.restoreAllMocks()
})

// ── askProjectQuestion ────────────────────────────────────────────────────

describe('askProjectQuestion', () => {
  it('calls POST on the correct answer route with JSON body', async () => {
    mockApiFetch.mockResolvedValue(
      jsonResponse(200, { status: 'answered', answer: 'Yes', citations: [] }),
    )

    await askProjectQuestion('proj-1', 'What is this?')

    expect(mockApiFetch).toHaveBeenCalledWith({
      path: '/api/v1/projects/proj-1/answer',
      method: 'POST',
      body: JSON.stringify({ question: 'What is this?' }),
      headers: { 'Content-Type': 'application/json' },
    })
  })

  it('returns parsed response with answer and citations on 200', async () => {
    const data = {
      status: 'answered',
      answer: 'The answer is 42.',
      citations: [
        {
          documentId: 'd1',
          documentChunkId: 'dc1',
          chunkIndex: 0,
          startOffset: 0,
          endOffset: 100,
          text: 'chunk text',
        },
      ],
    }
    mockApiFetch.mockResolvedValue(jsonResponse(200, data))

    const result = await askProjectQuestion('proj-1', 'What?')

    expect(result.status).toBe('answered')
    expect(result.answer).toBe('The answer is 42.')
    expect(result.citations).toHaveLength(1)
    expect(result.citations[0]!.documentId).toBe('d1')
    expect(result.citations[0]!.text).toBe('chunk text')
  })

  it('returns insufficient_evidence response on 200', async () => {
    const data = { status: 'insufficient_evidence', answer: null, citations: [] }
    mockApiFetch.mockResolvedValue(jsonResponse(200, data))

    const result = await askProjectQuestion('proj-1', 'Unknown topic?')

    expect(result.status).toBe('insufficient_evidence')
    expect(result.answer).toBeNull()
    expect(result.citations).toHaveLength(0)
  })

  it('throws ProjectNotFoundError on 404', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(404, {}))

    await expect(askProjectQuestion('proj-1', 'What?')).rejects.toBeInstanceOf(
      ProjectNotFoundError,
    )
  })

  it('throws RagApiError with server message on 400', async () => {
    mockApiFetch.mockResolvedValue(
      problemResponse(400, 'Question must contain at least one alphanumeric character.'),
    )

    const err = await askProjectQuestion('proj-1', '???').catch((e: unknown) => e)

    expect(err).toBeInstanceOf(RagApiError)
    expect((err as RagApiError).status).toBe(400)
    expect((err as RagApiError).serverMessage).toBe(
      'Question must contain at least one alphanumeric character.',
    )
  })

  it('throws RagApiError on 503 (provider unavailable)', async () => {
    mockApiFetch.mockResolvedValue(new Response(null, { status: 503 }))

    const err = await askProjectQuestion('proj-1', 'What?').catch((e: unknown) => e)

    expect(err).toBeInstanceOf(RagApiError)
    expect((err as RagApiError).status).toBe(503)
  })

  it('throws RagApiError on 502 (provider contract violation)', async () => {
    mockApiFetch.mockResolvedValue(new Response(null, { status: 502 }))

    const err = await askProjectQuestion('proj-1', 'What?').catch((e: unknown) => e)

    expect(err).toBeInstanceOf(RagApiError)
    expect((err as RagApiError).status).toBe(502)
  })

  it('encodes projectId in the URL', async () => {
    mockApiFetch.mockResolvedValue(
      jsonResponse(200, { status: 'answered', answer: 'OK', citations: [] }),
    )

    await askProjectQuestion('proj/special', 'What?')

    expect(mockApiFetch).toHaveBeenCalledWith(
      expect.objectContaining({
        path: '/api/v1/projects/proj%2Fspecial/answer',
      }),
    )
  })

  it('uses existing authenticated request infrastructure (apiFetch)', async () => {
    mockApiFetch.mockResolvedValue(
      jsonResponse(200, { status: 'answered', answer: 'OK', citations: [] }),
    )

    await askProjectQuestion('proj-1', 'Test')

    expect(mockApiFetch).toHaveBeenCalledTimes(1)
  })
})

// ── searchProjectDocuments ────────────────────────────────────────────────

describe('searchProjectDocuments', () => {
  it('calls POST on the correct search route with JSON body', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(200, { items: [] }))

    await searchProjectDocuments('proj-1', 'test query')

    expect(mockApiFetch).toHaveBeenCalledWith({
      path: '/api/v1/projects/proj-1/search',
      method: 'POST',
      body: JSON.stringify({ queryText: 'test query' }),
      headers: { 'Content-Type': 'application/json' },
    })
  })

  it('includes topK when provided', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(200, { items: [] }))

    await searchProjectDocuments('proj-1', 'test', 5)

    expect(mockApiFetch).toHaveBeenCalledWith({
      path: '/api/v1/projects/proj-1/search',
      method: 'POST',
      body: JSON.stringify({ queryText: 'test', topK: 5 }),
      headers: { 'Content-Type': 'application/json' },
    })
  })

  it('returns parsed response with search hits on 200', async () => {
    const data = {
      items: [
        {
          documentId: 'd1',
          documentChunkId: 'dc1',
          chunkIndex: 3,
          startOffset: 100,
          endOffset: 200,
          text: 'matching text',
        },
      ],
    }
    mockApiFetch.mockResolvedValue(jsonResponse(200, data))

    const result = await searchProjectDocuments('proj-1', 'test')

    expect(result.items).toHaveLength(1)
    expect(result.items[0]!.text).toBe('matching text')
    expect(result.items[0]!.chunkIndex).toBe(3)
  })

  it('throws ProjectNotFoundError on 404', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(404, {}))

    await expect(
      searchProjectDocuments('proj-1', 'test'),
    ).rejects.toBeInstanceOf(ProjectNotFoundError)
  })

  it('throws RagApiError on 503 (embedding service unavailable)', async () => {
    mockApiFetch.mockResolvedValue(new Response(null, { status: 503 }))

    const err = await searchProjectDocuments('proj-1', 'test').catch(
      (e: unknown) => e,
    )

    expect(err).toBeInstanceOf(RagApiError)
    expect((err as RagApiError).status).toBe(503)
  })

  it('throws RagApiError with server message on 400', async () => {
    mockApiFetch.mockResolvedValue(
      problemResponse(400, 'Query text is required.'),
    )

    const err = await searchProjectDocuments('proj-1', '').catch(
      (e: unknown) => e,
    )

    expect(err).toBeInstanceOf(RagApiError)
    expect((err as RagApiError).serverMessage).toBe('Query text is required.')
  })

  it('encodes projectId in the URL', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(200, { items: [] }))

    await searchProjectDocuments('proj/special', 'test')

    expect(mockApiFetch).toHaveBeenCalledWith(
      expect.objectContaining({
        path: '/api/v1/projects/proj%2Fspecial/search',
      }),
    )
  })
})

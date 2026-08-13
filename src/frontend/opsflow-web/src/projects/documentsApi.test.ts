import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../auth/apiClient', () => ({
  apiFetch: vi.fn(),
}))

import { apiFetch } from '../auth/apiClient'
import {
  listDocuments,
  uploadDocument,
  DocumentApiError,
  ProjectNotFoundError,
} from './documentsApi'

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

function textResponse(status: number, text: string): Response {
  return new Response(text, {
    status,
    headers: { 'Content-Type': 'text/plain' },
  })
}

beforeEach(() => {
  mockApiFetch.mockReset()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('listDocuments', () => {
  it('calls GET on the correct project documents route', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(200, { items: [] }))

    await listDocuments('proj-abc')

    expect(mockApiFetch).toHaveBeenCalledWith({
      path: '/api/v1/projects/proj-abc/documents',
      method: 'GET',
    })
  })

  it('returns parsed DocumentListResponse on 200', async () => {
    const data = {
      items: [
        {
          id: 'd1',
          originalFileName: 'report.pdf',
          contentType: 'application/pdf',
          sizeBytes: 1024,
          createdAt: '2026-08-01T00:00:00Z',
        },
      ],
    }
    mockApiFetch.mockResolvedValue(jsonResponse(200, data))

    const result = await listDocuments('proj-abc')

    expect(result.items).toHaveLength(1)
    expect(result.items[0]!.originalFileName).toBe('report.pdf')
  })

  it('throws ProjectNotFoundError on 404', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(404, {}))

    await expect(listDocuments('proj-abc')).rejects.toBeInstanceOf(
      ProjectNotFoundError,
    )
  })

  it('throws DocumentApiError with status on generic failure', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(500, {}))

    const err = await listDocuments('proj-abc').catch((e: unknown) => e)

    expect(err).toBeInstanceOf(DocumentApiError)
    expect((err as DocumentApiError).status).toBe(500)
  })
})

describe('uploadDocument', () => {
  it('calls POST to the correct project documents route', async () => {
    const doc = {
      id: 'd1',
      originalFileName: 'file.pdf',
      contentType: 'application/pdf',
      sizeBytes: 100,
      createdAt: '2026-08-01T00:00:00Z',
    }
    mockApiFetch.mockResolvedValue(jsonResponse(201, doc))
    const file = new File(['content'], 'file.pdf', { type: 'application/pdf' })

    await uploadDocument('proj-abc', file)

    expect(mockApiFetch).toHaveBeenCalledWith(
      expect.objectContaining({
        path: '/api/v1/projects/proj-abc/documents',
        method: 'POST',
      }),
    )
  })

  it('sends file as multipart "file" field with FormData body', async () => {
    const doc = {
      id: 'd1',
      originalFileName: 'file.pdf',
      contentType: 'application/pdf',
      sizeBytes: 100,
      createdAt: '2026-08-01T00:00:00Z',
    }
    mockApiFetch.mockResolvedValue(jsonResponse(201, doc))
    const file = new File(['content'], 'file.pdf', { type: 'application/pdf' })

    await uploadDocument('proj-abc', file)

    const call = mockApiFetch.mock.calls[0]![0]
    const body = call.body as FormData
    expect(body).toBeInstanceOf(FormData)
    expect(body.get('file')).toBe(file)
  })

  it('does not include organizationId or tenantId in the multipart body', async () => {
    const doc = {
      id: 'd1',
      originalFileName: 'file.pdf',
      contentType: 'application/pdf',
      sizeBytes: 100,
      createdAt: '2026-08-01T00:00:00Z',
    }
    mockApiFetch.mockResolvedValue(jsonResponse(201, doc))
    const file = new File(['content'], 'file.pdf', { type: 'application/pdf' })

    await uploadDocument('proj-abc', file)

    const call = mockApiFetch.mock.calls[0]![0]
    const body = call.body as FormData
    expect(body.get('organizationId')).toBeNull()
    expect(body.get('tenantId')).toBeNull()
    expect(body.get('projectId')).toBeNull()
  })

  it('returns parsed DocumentResponse on 201', async () => {
    const doc = {
      id: 'd1',
      originalFileName: 'report.pdf',
      contentType: 'application/pdf',
      sizeBytes: 2048,
      createdAt: '2026-08-02T00:00:00Z',
    }
    mockApiFetch.mockResolvedValue(jsonResponse(201, doc))
    const file = new File(['data'], 'report.pdf', { type: 'application/pdf' })

    const result = await uploadDocument('proj-abc', file)

    expect(result.id).toBe('d1')
    expect(result.originalFileName).toBe('report.pdf')
    expect(result.sizeBytes).toBe(2048)
  })

  it('throws ProjectNotFoundError on 404', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(404, {}))
    const file = new File(['data'], 'file.pdf', { type: 'application/pdf' })

    await expect(uploadDocument('proj-abc', file)).rejects.toBeInstanceOf(
      ProjectNotFoundError,
    )
  })

  it('throws DocumentApiError with status 400 on bad request', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(400, {}))
    const file = new File(['data'], 'file.pdf', { type: 'application/pdf' })

    const err = await uploadDocument('proj-abc', file).catch((e: unknown) => e)

    expect(err).toBeInstanceOf(DocumentApiError)
    expect((err as DocumentApiError).status).toBe(400)
  })

  it('throws DocumentApiError with status 413 on oversized file', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(413, {}))
    const file = new File(['data'], 'file.pdf', { type: 'application/pdf' })

    const err = await uploadDocument('proj-abc', file).catch((e: unknown) => e)

    expect(err).toBeInstanceOf(DocumentApiError)
    expect((err as DocumentApiError).status).toBe(413)
  })

  it('throws DocumentApiError with status 422 on unsupported type', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(422, {}))
    const file = new File(['data'], 'file.pdf', { type: 'application/pdf' })

    const err = await uploadDocument('proj-abc', file).catch((e: unknown) => e)

    expect(err).toBeInstanceOf(DocumentApiError)
    expect((err as DocumentApiError).status).toBe(422)
  })

  it('extracts safe validation message from problem+json detail', async () => {
    mockApiFetch.mockResolvedValue(
      problemResponse(422, 'Unsupported file type.'),
    )
    const file = new File(['data'], 'file.pdf', { type: 'application/pdf' })

    const err = await uploadDocument('proj-abc', file).catch((e: unknown) => e)

    expect(err).toBeInstanceOf(DocumentApiError)
    expect((err as DocumentApiError).serverMessage).toBe('Unsupported file type.')
  })

  it('extracts safe message from plain text response body', async () => {
    mockApiFetch.mockResolvedValue(textResponse(400, 'A non-empty file is required.'))
    const file = new File(['data'], 'file.pdf', { type: 'application/pdf' })

    const err = await uploadDocument('proj-abc', file).catch((e: unknown) => e)

    expect(err).toBeInstanceOf(DocumentApiError)
    expect((err as DocumentApiError).serverMessage).toBe('A non-empty file is required.')
  })

  it('throws DocumentApiError with null serverMessage on opaque failure', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(500, {}))
    const file = new File(['data'], 'file.pdf', { type: 'application/pdf' })

    const err = await uploadDocument('proj-abc', file).catch((e: unknown) => e)

    expect(err).toBeInstanceOf(DocumentApiError)
    expect((err as DocumentApiError).status).toBe(500)
    expect((err as DocumentApiError).serverMessage).toBeNull()
  })
})

describe('extractSafeMessage (via uploadDocument error paths)', () => {
  function errorResponse(status: number, body: string, contentType: string): Response {
    return new Response(body, {
      status,
      headers: { 'Content-Type': contentType },
    })
  }

  async function extractedMessage(response: Response): Promise<string | null> {
    mockApiFetch.mockResolvedValue(response)
    const file = new File(['data'], 'file.pdf', { type: 'application/pdf' })
    const err = await uploadDocument('proj-abc', file).catch((e: unknown) => e)
    expect(err).toBeInstanceOf(DocumentApiError)
    return (err as DocumentApiError).serverMessage
  }

  it('surfaces problem+json detail <=500 chars', async () => {
    const msg = await extractedMessage(
      errorResponse(422, JSON.stringify({ detail: 'Unsupported file type.' }), 'application/problem+json'),
    )
    expect(msg).toBe('Unsupported file type.')
  })

  it('surfaces problem+json title when detail is absent', async () => {
    const msg = await extractedMessage(
      errorResponse(422, JSON.stringify({ title: 'Validation Error' }), 'application/problem+json'),
    )
    expect(msg).toBe('Validation Error')
  })

  it('trims whitespace from problem+json detail', async () => {
    const msg = await extractedMessage(
      errorResponse(422, JSON.stringify({ detail: '  padded message  ' }), 'application/problem+json'),
    )
    expect(msg).toBe('padded message')
  })

  it('falls back to title when detail is only whitespace', async () => {
    const msg = await extractedMessage(
      errorResponse(422, JSON.stringify({ detail: '   ', title: 'Fallback title' }), 'application/problem+json'),
    )
    expect(msg).toBe('Fallback title')
  })

  it('rejects problem+json detail >500 chars', async () => {
    const long = 'x'.repeat(501)
    const msg = await extractedMessage(
      errorResponse(422, JSON.stringify({ detail: long }), 'application/problem+json'),
    )
    expect(msg).toBeNull()
  })

  it('rejects problem+json title >500 chars when detail absent', async () => {
    const long = 'x'.repeat(501)
    const msg = await extractedMessage(
      errorResponse(422, JSON.stringify({ title: long }), 'application/problem+json'),
    )
    expect(msg).toBeNull()
  })

  it('surfaces text/plain body <=500 chars', async () => {
    const msg = await extractedMessage(
      errorResponse(400, 'A non-empty file is required.', 'text/plain'),
    )
    expect(msg).toBe('A non-empty file is required.')
  })

  it('trims whitespace from text/plain body', async () => {
    const msg = await extractedMessage(
      errorResponse(400, '  trimmed  ', 'text/plain'),
    )
    expect(msg).toBe('trimmed')
  })

  it('rejects text/plain body >500 chars', async () => {
    const long = 'x'.repeat(501)
    const msg = await extractedMessage(
      errorResponse(400, long, 'text/plain'),
    )
    expect(msg).toBeNull()
  })

  it('rejects short text/html body', async () => {
    const msg = await extractedMessage(
      errorResponse(400, '<html><body>Error</body></html>', 'text/html'),
    )
    expect(msg).toBeNull()
  })

  it('rejects text/xml body', async () => {
    const msg = await extractedMessage(
      errorResponse(400, '<error>fail</error>', 'text/xml'),
    )
    expect(msg).toBeNull()
  })

  it('rejects application/json body', async () => {
    const msg = await extractedMessage(
      errorResponse(500, JSON.stringify({ error: 'internal' }), 'application/json'),
    )
    expect(msg).toBeNull()
  })

  it('rejects unknown/binary content type', async () => {
    const msg = await extractedMessage(
      errorResponse(500, '\x00\x01\x02', 'application/octet-stream'),
    )
    expect(msg).toBeNull()
  })
})

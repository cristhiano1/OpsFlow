import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../auth/apiClient', () => ({
  apiFetch: vi.fn(),
}))

import { apiFetch } from '../auth/apiClient'
import { createProject, listProjects } from './projectsApi'

const mockApiFetch = vi.mocked(apiFetch)

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

beforeEach(() => {
  mockApiFetch.mockReset()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('listProjects', () => {
  it('calls GET /api/v1/projects and returns items', async () => {
    const data = {
      items: [
        { id: 'p1', name: 'Alpha', description: null, createdAt: '2026-08-10T00:00:00Z' },
      ],
    }
    mockApiFetch.mockResolvedValue(jsonResponse(200, data))

    const result = await listProjects()

    expect(mockApiFetch).toHaveBeenCalledWith({
      path: '/api/v1/projects',
      method: 'GET',
    })
    expect(result.items).toHaveLength(1)
    expect(result.items[0]!.name).toBe('Alpha')
  })

  it('throws on non-ok response', async () => {
    mockApiFetch.mockResolvedValue(jsonResponse(500, {}))

    await expect(listProjects()).rejects.toThrow('Failed to list projects: 500')
  })
})

describe('createProject', () => {
  it('calls POST /api/v1/projects with name and description', async () => {
    const created = { id: 'p2', name: 'Beta', description: 'A desc', createdAt: '2026-08-10T00:00:00Z' }
    mockApiFetch.mockResolvedValue(jsonResponse(201, created))

    const result = await createProject({ name: 'Beta', description: 'A desc' })

    expect(mockApiFetch).toHaveBeenCalledWith({
      path: '/api/v1/projects',
      method: 'POST',
      body: JSON.stringify({ name: 'Beta', description: 'A desc' }),
      headers: { 'Content-Type': 'application/json' },
    })
    expect(result.name).toBe('Beta')
  })

  it('throws on non-ok response', async () => {
    mockApiFetch.mockResolvedValue(new Response('Bad Request', { status: 400 }))

    await expect(
      createProject({ name: '' }),
    ).rejects.toThrow('Failed to create project: 400')
  })
})

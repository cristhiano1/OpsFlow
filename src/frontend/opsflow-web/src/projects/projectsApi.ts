import { apiFetch } from '../auth/apiClient'

export interface ProjectResponse {
  id: string
  name: string
  description: string | null
  createdAt: string
}

export interface ProjectListResponse {
  items: ProjectResponse[]
}

export interface CreateProjectRequest {
  name: string
  description?: string | null
}

export async function listProjects(): Promise<ProjectListResponse> {
  const response = await apiFetch({
    path: '/api/v1/projects',
    method: 'GET',
  })
  if (!response.ok) {
    throw new Error(`Failed to list projects: ${response.status}`)
  }
  return (await response.json()) as ProjectListResponse
}

export async function createProject(
  request: CreateProjectRequest,
): Promise<ProjectResponse> {
  const response = await apiFetch({
    path: '/api/v1/projects',
    method: 'POST',
    body: JSON.stringify(request),
    headers: { 'Content-Type': 'application/json' },
  })
  if (!response.ok) {
    const text = await response.text()
    throw new Error(`Failed to create project: ${response.status} ${text}`)
  }
  return (await response.json()) as ProjectResponse
}

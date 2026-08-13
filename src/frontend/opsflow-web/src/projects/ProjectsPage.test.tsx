import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('./projectsApi', () => ({
  listProjects: vi.fn(),
  createProject: vi.fn(),
}))

import { listProjects, createProject } from './projectsApi'
import { ProjectsPage } from './ProjectsPage'

function renderProjectsPage() {
  return render(
    <MemoryRouter initialEntries={['/projects']}>
      <Routes>
        <Route path="/projects" element={<ProjectsPage />} />
      </Routes>
    </MemoryRouter>,
  )
}

const mockListProjects = vi.mocked(listProjects)
const mockCreateProject = vi.mocked(createProject)

beforeEach(() => {
  mockListProjects.mockReset()
  mockCreateProject.mockReset()
})

describe('ProjectsPage', () => {
  it('shows loading state initially', () => {
    mockListProjects.mockReturnValue(new Promise(() => {}))
    renderProjectsPage()
    expect(screen.getByText('Loading projects…')).toBeInTheDocument()
  })

  it('shows empty state when no projects', async () => {
    mockListProjects.mockResolvedValue({ items: [] })
    renderProjectsPage()
    await waitFor(() => {
      expect(screen.getByText('No projects yet. Create one above.')).toBeInTheDocument()
    })
  })

  it('shows projects when loaded', async () => {
    mockListProjects.mockResolvedValue({
      items: [
        { id: 'p1', name: 'Alpha', description: 'First project', createdAt: '2026-08-10T00:00:00Z' },
      ],
    })
    renderProjectsPage()
    await waitFor(() => {
      expect(screen.getByText('Alpha')).toBeInTheDocument()
    })
    expect(screen.getByText('First project')).toBeInTheDocument()
  })

  it('shows error state on load failure', async () => {
    mockListProjects.mockRejectedValue(new Error('Network error'))
    renderProjectsPage()
    await waitFor(() => {
      expect(screen.getByText('Network error')).toBeInTheDocument()
    })
  })

  it('creates a project and refreshes list', async () => {
    const user = userEvent.setup()
    mockListProjects
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValueOnce({
        items: [
          { id: 'p1', name: 'New Project', description: null, createdAt: '2026-08-10T00:00:00Z' },
        ],
      })
    mockCreateProject.mockResolvedValue({
      id: 'p1', name: 'New Project', description: null, createdAt: '2026-08-10T00:00:00Z',
    })

    renderProjectsPage()
    await waitFor(() => {
      expect(screen.getByText('No projects yet. Create one above.')).toBeInTheDocument()
    })

    await user.type(screen.getByLabelText('Name'), 'New Project')
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await waitFor(() => {
      expect(screen.getByText('New Project')).toBeInTheDocument()
    })
    expect(mockCreateProject).toHaveBeenCalledWith({
      name: 'New Project',
      description: null,
    })
  })

  it('shows form error when create fails', async () => {
    const user = userEvent.setup()
    mockListProjects.mockResolvedValue({ items: [] })
    mockCreateProject.mockRejectedValue(new Error('Validation failed'))

    renderProjectsPage()
    await waitFor(() => {
      expect(screen.getByText('No projects yet. Create one above.')).toBeInTheDocument()
    })

    await user.type(screen.getByLabelText('Name'), 'Bad')
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await waitFor(() => {
      expect(screen.getByText('Validation failed')).toBeInTheDocument()
    })
  })

  it('disables submit button while submitting', async () => {
    const user = userEvent.setup()
    mockListProjects.mockResolvedValue({ items: [] })
    mockCreateProject.mockReturnValue(new Promise(() => {}))

    renderProjectsPage()
    await waitFor(() => {
      expect(screen.getByText('No projects yet. Create one above.')).toBeInTheDocument()
    })

    await user.type(screen.getByLabelText('Name'), 'Test')
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(screen.getByRole('button', { name: 'Creating…' })).toBeDisabled()
  })

  it('disables submit button when name is empty', async () => {
    mockListProjects.mockResolvedValue({ items: [] })
    renderProjectsPage()
    await waitFor(() => {
      expect(screen.getByText('No projects yet. Create one above.')).toBeInTheDocument()
    })
    expect(screen.getByRole('button', { name: 'Create project' })).toBeDisabled()
  })

  it('renders the Projects heading', async () => {
    mockListProjects.mockResolvedValue({ items: [] })
    renderProjectsPage()
    expect(screen.getByRole('heading', { name: 'Projects' })).toBeInTheDocument()
  })

  it('stale initial load does not overwrite fresh post-create list', async () => {
    const user = userEvent.setup()

    type Resolve<T> = (value: T) => void
    function deferred<T>() {
      let resolve!: Resolve<T>
      const promise = new Promise<T>(r => { resolve = r })
      return { promise, resolve }
    }

    const initialLoad = deferred<{ items: { id: string; name: string; description: string | null; createdAt: string }[] }>()
    const postCreateLoad = deferred<{ items: { id: string; name: string; description: string | null; createdAt: string }[] }>()

    mockListProjects
      .mockReturnValueOnce(initialLoad.promise)
      .mockReturnValueOnce(postCreateLoad.promise)
    mockCreateProject.mockResolvedValue({
      id: 'p1', name: 'Fresh', description: null, createdAt: '2026-08-11T00:00:00Z',
    })

    renderProjectsPage()
    expect(screen.getByText('Loading projects…')).toBeInTheDocument()

    await user.type(screen.getByLabelText('Name'), 'Fresh')
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    postCreateLoad.resolve({
      items: [
        { id: 'p1', name: 'Fresh', description: null, createdAt: '2026-08-11T00:00:00Z' },
      ],
    })

    await waitFor(() => {
      expect(screen.getByText('Fresh')).toBeInTheDocument()
    })

    await act(async () => {
      initialLoad.resolve({ items: [] })
      await initialLoad.promise
    })

    expect(screen.getByText('Fresh')).toBeInTheDocument()
    expect(screen.queryByText('No projects yet. Create one above.')).not.toBeInTheDocument()
  })

  it('project name is a link to /projects/:projectId', async () => {
    mockListProjects.mockResolvedValue({
      items: [
        { id: 'proj-99', name: 'My Project', description: null, createdAt: '2026-08-10T00:00:00Z' },
      ],
    })
    renderProjectsPage()
    await waitFor(() => {
      expect(screen.getByText('My Project')).toBeInTheDocument()
    })
    const link = screen.getByRole('link', { name: 'My Project' })
    expect(link).toHaveAttribute('href', '/projects/proj-99')
  })

  it('stale initial load error does not overwrite fresh list', async () => {
    const user = userEvent.setup()

    type Reject = (reason: Error) => void
    function deferredReject<T>() {
      let reject!: Reject
      const promise = new Promise<T>((_, r) => { reject = r as Reject })
      return { promise, reject }
    }

    const initialLoad = deferredReject<{ items: { id: string; name: string; description: string | null; createdAt: string }[] }>()

    mockListProjects
      .mockReturnValueOnce(initialLoad.promise)
      .mockResolvedValueOnce({
        items: [
          { id: 'p1', name: 'Created', description: null, createdAt: '2026-08-11T00:00:00Z' },
        ],
      })
    mockCreateProject.mockResolvedValue({
      id: 'p1', name: 'Created', description: null, createdAt: '2026-08-11T00:00:00Z',
    })

    renderProjectsPage()

    await user.type(screen.getByLabelText('Name'), 'Created')
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await waitFor(() => {
      expect(screen.getByText('Created')).toBeInTheDocument()
    })

    await act(async () => {
      initialLoad.reject(new Error('Stale network error'))
      await initialLoad.promise.catch(() => {})
    })

    expect(screen.getByText('Created')).toBeInTheDocument()
    expect(screen.queryByText('Stale network error')).not.toBeInTheDocument()
  })
})

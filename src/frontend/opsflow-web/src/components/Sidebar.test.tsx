import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect } from 'vitest'
import { Sidebar } from './Sidebar'

function renderSidebar(initialEntries: string[] = ['/']) {
  return render(
    <MemoryRouter initialEntries={initialEntries}>
      <Sidebar />
    </MemoryRouter>,
  )
}

describe('Sidebar', () => {
  it('renders primary navigation landmark', () => {
    renderSidebar()
    expect(
      screen.getByRole('navigation', { name: 'Primary navigation' }),
    ).toBeInTheDocument()
  })

  it('renders Dashboard link pointing to /', () => {
    renderSidebar()
    const link = screen.getByRole('link', { name: 'Dashboard' })
    expect(link).toBeInTheDocument()
    expect(link).toHaveAttribute('href', '/')
  })

  it('marks Dashboard as active when location is /', () => {
    renderSidebar(['/'])
    const link = screen.getByRole('link', { name: 'Dashboard' })
    expect(link.className).toContain('active')
  })

  it('does not mark Dashboard as active on a different path', () => {
    renderSidebar(['/other'])
    const link = screen.getByRole('link', { name: 'Dashboard' })
    expect(link.className).not.toContain('active')
  })
})

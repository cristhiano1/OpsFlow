import { render, screen } from '@testing-library/react'
import { describe, it, expect } from 'vitest'
import { DashboardPage } from './DashboardPage'

describe('DashboardPage', () => {
  it('renders Dashboard heading', () => {
    render(<DashboardPage />)
    expect(
      screen.getByRole('heading', { name: 'Dashboard' }),
    ).toBeInTheDocument()
  })

  it('renders workspace introduction', () => {
    render(<DashboardPage />)
    expect(
      screen.getByText(/welcome to your opsflow workspace/i),
    ).toBeInTheDocument()
  })
})

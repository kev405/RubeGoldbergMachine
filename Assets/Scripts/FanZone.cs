using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
public class FanZone : MonoBehaviour
{
    [SerializeField] private float force = 15f;
    [SerializeField] private Transform blades;
    [SerializeField] private float bladeSpeed = 900f;
    [SerializeField] private ParticleSystem windVfx;

    private bool on;

    public void TurnOn()
    {
        on = true;
        if (windVfx != null) windVfx.Play();
    }

    private void Update()
    {
        if (on && blades != null)
            blades.Rotate(0f, 0f, bladeSpeed * Time.deltaTime, Space.Self);
    }

    private void OnTriggerStay(Collider other)
    {
        if (!on || other.attachedRigidbody == null) return;
        other.attachedRigidbody.AddForce(transform.forward * force, ForceMode.Force);
    }
}
